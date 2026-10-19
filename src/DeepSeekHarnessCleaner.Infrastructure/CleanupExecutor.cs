using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>逐项删除结果状态（TC-11/TC-16：部分成功如实报告）。</summary>
public enum ItemStatus
{
    Deleted,
    /// <summary>复验失败或门禁外单项跳过：文件保持原样。</summary>
    Skipped,
    /// <summary>删除尝试失败（权限/IO）：如实记录。</summary>
    Failed,
}

/// <summary>单个目标的执行结果。DeletedBytes 仅 Deleted 时非 0。</summary>
public sealed record ItemOutcome(
    string AbsolutePath,
    ItemStatus Status,
    string? Reason,
    long DeletedBytes);

/// <summary>
/// 执行结果：Aborted=全局门禁拒绝，此时 Outcomes 为空（零删除）；
/// 未中止时逐项结果如实收集，部分失败不影响其余项。
/// </summary>
public sealed record CleanupExecutionResult(
    bool Aborted,
    string? AbortCode,
    IReadOnlyList<ItemOutcome> Outcomes)
{
    public int DeletedCount => Outcomes.Count(static o => o.Status == ItemStatus.Deleted);
    public int SkippedCount => Outcomes.Count(static o => o.Status == ItemStatus.Skipped);
    public int FailedCount => Outcomes.Count(static o => o.Status == ItemStatus.Failed);
    public long DeletedBytes => Outcomes.Sum(static o => o.DeletedBytes);
    public bool AllSucceeded => !Aborted && Outcomes.Count > 0 && Outcomes.All(static o => o.Status == ItemStatus.Deleted);
}

/// <summary>
/// T-26 备份门禁与逐文件删除执行器（TC-11,TC-15,TC-16）。
/// 全局门禁（任一失败即中止、零删除）：用户确认 → 备份存在且 ZIP 重校验通过 →
/// Harness 进程探测仅 Inactive 放行 → 根指纹与计划一致。
/// 逐项复验（design.md §7.5）：PathGuard 包含/reparse → 普通文件 → 长度/mtime/SHA-256 与计划比对，
/// 变化即跳过该项；删除失败（权限/IO）如实记录，不中断其余项。
/// </summary>
public sealed class CleanupExecutor
{
    public const string GateNotConfirmedCode = "GATE_NOT_CONFIRMED";
    public const string GateBackupRequiredCode = "GATE_BACKUP_REQUIRED";
    public const string GateBackupInvalidCode = "GATE_BACKUP_INVALID";
    public const string GateProcessCode = "GATE_PROCESS_NOT_INACTIVE";
    public const string GateFingerprintCode = "GATE_FINGERPRINT_CHANGED";
    public const string CanceledCode = "CANCELED";

    private readonly PathGuard _pathGuard = new();
    private readonly BackupService _backupService = new();
    private readonly HarnessProcessProbe _probe;

    public CleanupExecutor(HarnessProcessProbe? probe = null) => _probe = probe ?? new HarnessProcessProbe();

    /// <summary>
    /// 执行计划。<paramref name="manifest"/> 与 <paramref name="backup"/> 来自 T-21/T-22；
    /// N-04（D1/D4）后会话/附件计划 RequiresBackup=false：备份可选，但提供备份（两者齐备）时仍重校验 ZIP
    /// （损坏/缺失/签名不符即 GATE_BACKUP_INVALID 中止）；RequiresBackup=true 的计划（如调用方显式要求）仍必须齐备。
    /// 确认、进程门禁、根指纹与逐项路径/指纹复验不受影响。
    /// </summary>
    public CleanupExecutionResult Execute(
        CleanupPlan plan,
        BackupManifest? manifest,
        VerifiedBackup? backup,
        string root,
        bool userConfirmed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!userConfirmed)
        {
            return new CleanupExecutionResult(true, GateNotConfirmedCode, []);
        }

        if (plan.RequiresBackup && (manifest is null || backup is null))
        {
            return new CleanupExecutionResult(true, GateBackupRequiredCode, []);
        }

        if (manifest is not null && backup is not null)
        {
            var verify = _backupService.VerifyZip(backup.ZipPath, manifest);
            if (!verify.Success)
            {
                return new CleanupExecutionResult(true, $"{GateBackupInvalidCode}:{verify.ErrorCode}", []);
            }
        }

        var probeResult = _probe.Probe();
        if (!probeResult.AllowsWrite)
        {
            return new CleanupExecutionResult(true, $"{GateProcessCode}:{probeResult.State}", []);
        }

        if (!string.Equals(ComputeRootFingerprint(root), plan.RootFingerprint, StringComparison.Ordinal))
        {
            return new CleanupExecutionResult(true, GateFingerprintCode, []);
        }

        var outcomes = new List<ItemOutcome>(plan.Targets.Count);
        foreach (var target in plan.Targets)
        {
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(new ItemOutcome(target.AbsolutePath, ItemStatus.Skipped, CanceledCode, 0));
                continue;
            }

            outcomes.Add(DeleteOne(root, target));
        }

        return new CleanupExecutionResult(false, null, outcomes);
    }

    private ItemOutcome DeleteOne(string root, CleanupTarget target)
    {
        var check = _pathGuard.Check(root, target.AbsolutePath, allowDirectory: false);
        if (!check.IsAllowed)
        {
            return new ItemOutcome(target.AbsolutePath, ItemStatus.Skipped, check.ErrorCode ?? "PATH_REJECTED", 0);
        }

        var full = check.FullPath;
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return new ItemOutcome(full, ItemStatus.Skipped, "MISSING_FILE", 0);
        }

        var attributes = info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new ItemOutcome(full, ItemStatus.Skipped, "PATH_REPARSE", 0);
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return new ItemOutcome(full, ItemStatus.Skipped, "NOT_REGULAR_FILE", 0);
        }

        if (info.Length != target.ExpectedLength)
        {
            return new ItemOutcome(full, ItemStatus.Skipped, "CHANGED_LENGTH", 0);
        }

        if (info.LastWriteTimeUtc != target.ExpectedWriteUtc.UtcDateTime)
        {
            return new ItemOutcome(full, ItemStatus.Skipped, "CHANGED_MTIME", 0);
        }

        if (target.ExpectedSha256 is not null)
        {
            string sha;
            try
            {
                sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return new ItemOutcome(full, ItemStatus.Skipped, "UNREADABLE_FILE", 0);
            }

            if (!string.Equals(sha, target.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ItemOutcome(full, ItemStatus.Skipped, "CHANGED_SHA256", 0);
            }
        }

        try
        {
            File.Delete(full);
            return new ItemOutcome(full, ItemStatus.Deleted, null, target.ExpectedLength);
        }
        catch (UnauthorizedAccessException)
        {
            return new ItemOutcome(full, ItemStatus.Failed, "PERMISSION_DENIED", 0);
        }
        catch (IOException)
        {
            return new ItemOutcome(full, ItemStatus.Failed, "IO_FAILURE", 0);
        }
    }

    /// <summary>与 T-24 CleanupPlanner 同构：规范化根完整路径 UTF-8 SHA-256 小写 hex。</summary>
    private static string ComputeRootFingerprint(string root)
    {
        var full = Path.GetFullPath(root.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
    }
}
