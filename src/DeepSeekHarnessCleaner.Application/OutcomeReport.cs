using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>报告项状态（与执行器逐项状态一一对应；UI 层映射，Application 不依赖 Infrastructure）。</summary>
public enum OutcomeItemStatus
{
    Deleted,
    Skipped,
    Failed,
}

/// <summary>
/// 报告单项（REQ-016）：UI 展示用。<see cref="DisplayPath"/> 仅出现在 UI，
/// 绝不写入 <see cref="OutcomeReport.BuildLog"/> 生成的脱敏日志。
/// </summary>
public sealed record OutcomeItem(
    string DisplayPath,
    CleanupCategory Category,
    string? OwnerSessionId,
    OutcomeItemStatus Status,
    string? ErrorCode,
    long FreedBytes);

/// <summary>报告输入：由 UI 层从执行结果 + 计划 + 备份事实组装（纯内存，零网络）。</summary>
public sealed record OutcomeReportInput(
    bool Aborted,
    string? AbortCode,
    string? BackupZipPath,
    string? BackupSignature,
    int BackupEntryCount,
    long BackupTotalBytes,
    IReadOnlyList<OutcomeItem> Items);

/// <summary>
/// T-31 不泄露正文的执行报告（REQ-016，TC-16/TC-18）：
/// UI 显示每项成功/跳过/失败、实际释放字节、备份位置与错误码；
/// <see cref="BuildLog"/> 产出脱敏结构化日志——只含固定事件名、白名单 token、类别、状态与数值，
/// 路径、会话正文、自由文本与任何不匹配白名单的 token 一律不进入日志。
/// </summary>
public sealed class OutcomeReport
{
    /// <summary>会话 ID 白名单（文件名派生 ID 形态）；不匹配则日志记 "-"。</summary>
    private static readonly Regex SafeSessionId = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>错误码白名单（大写字母/数字/下划线/点/冒号/连字符，如 GATE_BACKUP_INVALID:SEC-03）。</summary>
    private static readonly Regex SafeErrorCode = new("^[A-Za-z0-9_.:-]{1,80}$", RegexOptions.CultureInvariant);

    /// <summary>备份 ZIP 文件名白名单（仅文件名，不含目录）。</summary>
    private static readonly Regex SafeZipName = new(@"^[A-Za-z0-9._-]{1,128}\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>清单签名白名单：SHA-256 小写 hex。</summary>
    private static readonly Regex SafeSignature = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public OutcomeReport(OutcomeReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
        Items = input.Items;
        Aborted = input.Aborted;
        AbortCode = input.AbortCode;
        BackupZipPath = input.BackupZipPath;
        BackupSignature = input.BackupSignature;
        BackupEntryCount = Math.Max(0, input.BackupEntryCount);
        BackupTotalBytes = Math.Max(0, input.BackupTotalBytes);
    }

    public OutcomeReportInput Input { get; }

    public IReadOnlyList<OutcomeItem> Items { get; }

    public bool Aborted { get; }

    public string? AbortCode { get; }

    public string? BackupZipPath { get; }

    public string? BackupSignature { get; }

    public int BackupEntryCount { get; }

    public long BackupTotalBytes { get; }

    public int DeletedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Deleted);
    public int SkippedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Skipped);
    public int FailedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Failed);

    /// <summary>实际释放字节合计（仅 Deleted 项计入）。</summary>
    public long FreedBytes => Items.Sum(static i => i.FreedBytes);

    /// <summary>部分失败：未中止且存在跳过或失败项（TC-16 场景）。</summary>
    public bool PartialFailure => !Aborted && (SkippedCount + FailedCount) > 0;

    /// <summary>
    /// 脱敏结构化日志（每行 key=value）：1 行 run_summary + 1 行 backup + 每项 1 行 item。
    /// 与 UI 显示同源同数：逐项状态/错误码/字节一致；不含路径、正文、自由文本。
    /// </summary>
    public string BuildLog(DateTimeOffset? nowUtc = null)
    {
        var ts = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();

        sb.Append("ts=").Append(ts).Append(" event=run_summary")
            .Append(" aborted=").Append(Aborted ? "true" : "false");
        if (Aborted && AbortCode is { Length: > 0 })
        {
            sb.Append(" code=").Append(SafeCode(AbortCode));
        }

        sb.Append(" deleted=").Append(DeletedCount)
            .Append(" skipped=").Append(SkippedCount)
            .Append(" failed=").Append(FailedCount)
            .Append(" freed_bytes=").Append(FreedBytes)
            .Append(" items=").Append(Items.Count)
            .Append('\n');

        sb.Append("ts=").Append(ts).Append(" event=backup")
            .Append(" zip_name=").Append(SafeZipNameInput(BackupZipPath))
            .Append(" entries=").Append(BackupEntryCount)
            .Append(" bytes=").Append(BackupTotalBytes)
            .Append(" sig=").Append(BackupSignature is { Length: > 0 } sig && SafeSignature.IsMatch(sig) ? sig : "-")
            .Append('\n');

        foreach (var item in Items)
        {
            sb.Append("ts=").Append(ts).Append(" event=item")
                .Append(" category=").Append(item.Category.ToString())
                .Append(" status=").Append(item.Status.ToString().ToLowerInvariant())
                .Append(" code=").Append(item.ErrorCode is { Length: > 0 } code ? SafeCode(code) : "-")
                .Append(" bytes=").Append(item.FreedBytes)
                .Append(" session=").Append(item.OwnerSessionId is { Length: > 0 } id && SafeSessionId.IsMatch(id) ? id : "-")
                .Append('\n');
        }

        return sb.ToString();
    }

    private static string SafeCode(string code) =>
        SafeErrorCode.IsMatch(code) ? code : "UNSAFE_CODE";

    /// <summary>备份位置脱敏：日志只记文件名（白名单内），目录部分（可能含用户名）不出现。</summary>
    private static string SafeZipNameInput(string? zipPath)
    {
        if (zipPath is not { Length: > 0 })
        {
            return "-";
        }

        var name = Path.GetFileName(zipPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SafeZipName.IsMatch(name) ? name : "-";
    }
}
