using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 附件候选（Application 层输入记录；调用方从数据根 attachments/ 只读枚举，
/// 并携带 T-15 AttachmentIndex 的引用证据状态）。
/// </summary>
public sealed record AttachmentCandidate(
    string FullPath,
    AttachmentEvidenceStatus EvidenceStatus,
    bool IsRegularFile,
    long ExpectedLength,
    DateTimeOffset ExpectedWriteUtc,
    string? ExpectedSha256);

/// <summary>未进入附件计划的候选及原因。</summary>
public sealed record AttachmentExclusion(string FullPath, string Reason, string? Detail = null);

/// <summary>范围内附件的引用证据状态标注（Unknown 必须明确标出，不得当作孤儿）。</summary>
public sealed record AttachmentStatusNote(string FullPath, AttachmentEvidenceStatus Status);

/// <summary>附件清理计划结果（N-04/D1：RequiresBackup=false，备份可选；确认仍必需）。</summary>
public sealed record AttachmentCleanupPlanResult(
    string? RootErrorCode,
    CleanupPlan? Plan,
    IReadOnlyList<AttachmentExclusion> Exclusions,
    IReadOnlyList<AttachmentStatusNote> Statuses)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// T-29 显式附件备份与独立清理（TC-14，design.md §5）：
/// 仅数据根 attachments/ 下的普通文件、且必须出现在人工选择集合中才进入计划；
/// 不随会话自动删除——无任何自动加入机制，共享/被引用附件同样只有显式选择才进入；
/// 引用证据状态（含 Unknown）对范围内候选逐一标注；非普通文件（目录/软链等）拒绝；
/// 计划 RequiresBackup=false（N-04/D1：附件删除备份可选），确认门禁与执行器逐项复验保留；
/// 提供备份时仍走 T-26 executor 的 ZIP 校验通道。纯内存零 IO。
/// </summary>
public sealed class AttachmentCleanupSelection
{
    public const string AttachmentsDirName = "attachments";

    public const string NotSelectedReason = "ATTACHMENT_NOT_SELECTED";
    public const string NotRegularFileReason = "ATTACHMENT_NOT_REGULAR_FILE";
    public const string OutOfScopeReason = "OUT_OF_ATTACHMENTS_SCOPE";
    public const string DuplicateReason = "ATTACHMENT_DUPLICATE_PATH";

    public AttachmentCleanupPlanResult Select(
        string dataRoot,
        IReadOnlyList<AttachmentCandidate> candidates,
        IReadOnlyList<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        string rootFull;
        try
        {
            rootFull = NormalizeRoot(dataRoot);
        }
        catch (ArgumentException)
        {
            return new AttachmentCleanupPlanResult("ROOT_INVALID", null, [], []);
        }

        var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<AttachmentExclusion>();
        var statuses = new List<AttachmentStatusNote>();
        var staged = new List<CleanupTarget>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var containError = TryContain(rootFull, candidate.FullPath, out var full);
            if (containError is not null)
            {
                exclusions.Add(new AttachmentExclusion(candidate.FullPath, "ATTACHMENT_" + containError));
                continue;
            }

            var relative = Path.GetRelativePath(rootFull, full);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length < 2
                || !string.Equals(segments[0], AttachmentsDirName, StringComparison.OrdinalIgnoreCase))
            {
                // 数据根下 sessions/、storages/ 等其他域一律不属于附件清理范围。
                exclusions.Add(new AttachmentExclusion(full, OutOfScopeReason, relative));
                continue;
            }

            if (!candidate.IsRegularFile)
            {
                // 目录级附件、外部软链和未知类型禁止删除（design.md §5）。
                exclusions.Add(new AttachmentExclusion(full, NotRegularFileReason, relative));
                continue;
            }

            // 范围内候选一律标注引用证据状态（Unknown 明确标出，不推断孤儿）。
            statuses.Add(new AttachmentStatusNote(full, candidate.EvidenceStatus));

            if (!selected.Contains(full))
            {
                // 不随会话自动删除：被引用/共享/ConfirmedUnreferenced 均不自动加入，仅人工选择进入。
                exclusions.Add(new AttachmentExclusion(full, NotSelectedReason, candidate.EvidenceStatus.ToString()));
                continue;
            }

            if (!seenPaths.Add(full))
            {
                exclusions.Add(new AttachmentExclusion(full, DuplicateReason));
                continue;
            }

            staged.Add(new CleanupTarget(
                full,
                CleanupCategory.Attachment,
                OwnerSessionId: null,
                candidate.ExpectedLength,
                candidate.ExpectedWriteUtc,
                candidate.ExpectedSha256));
        }

        staged.Sort(static (a, b) => string.CompareOrdinal(a.AbsolutePath, b.AbsolutePath));

        // N-04（D1）：附件删除备份改为可选；确认必需（executor userConfirmed 门禁保留）。
        var plan = new CleanupPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ComputeRootFingerprint(rootFull),
            staged,
            RequiresBackup: false);

        return new AttachmentCleanupPlanResult(null, plan, exclusions, statuses);
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("根路径为空", nameof(root));
        }

        var full = Path.GetFullPath(root.Trim());
        if (Path.GetPathRoot(full) is not { Length: > 1 }
            || string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("根不得为驱动器根", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>与 T-24/T-27/T-28 同构的根内复验。</summary>
    private static string? TryContain(string rootFull, string candidatePath, out string full)
    {
        full = candidatePath;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return "INVALID";
        }

        if (candidatePath.Contains("..", StringComparison.Ordinal)
            && candidatePath.Split('\\', '/').Any(static segment => segment == ".."))
        {
            return "PATH_TRAVERSAL";
        }

        try
        {
            full = Path.GetFullPath(candidatePath);
        }
        catch (ArgumentException)
        {
            return "INVALID";
        }

        var relative = Path.GetRelativePath(rootFull, full);
        if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative == "..")
        {
            return "CROSS_ROOT";
        }

        return null;
    }

    private static string ComputeRootFingerprint(string rootFull) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootFull))).ToLowerInvariant();
}
