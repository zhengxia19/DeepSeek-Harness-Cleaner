using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 扫描源端口（由 Infrastructure 适配器实现：发现 + 受限头部读取）。
/// Application 不依赖 Infrastructure，只消费 Domain 事实（architecture.md 分层）。
/// </summary>
public interface ISessionScanSource
{
    SessionScanData Scan(string sessionRoot);
}

/// <summary>会话库存扫描合同（interfaces.md ISessionInventory）。</summary>
public interface ISessionInventory
{
    Task<IReadOnlyList<SessionSummary>> ScanAsync(ResolvedRoots roots, CancellationToken ct);
}

/// <summary>
/// 库存与磁盘统计结果。TotalBytes 为全部逻辑会话物理文件求和；
/// AnomalousFiles 为不属于任何会话目录的异常文件（可解释、不自动清理）。
/// </summary>
public sealed record InventoryResult(
    string? RootErrorCode,
    IReadOnlyList<SessionSummary> Sessions,
    IReadOnlyList<AnomalousFile> AnomalousFiles)
{
    public long TotalBytes { get; init; }
}

/// <summary>
/// 会话库存与磁盘统计（design.md §1/§2/§3，T-10）。按经验证身份把同一目录的
/// 多代际物理文件归并为一条逻辑会话；容量按所有物理文件求和；
/// 单个文件损坏只影响该会话状态，不中断扫描。
/// </summary>
public sealed class InventoryService : ISessionInventory
{
    private readonly ISessionScanSource _source;

    public InventoryService(ISessionScanSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public InventoryResult Scan(string sessionRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var data = _source.Scan(sessionRoot);
        if (data.RootErrorCode is not null)
            return new InventoryResult(data.RootErrorCode, [], []);

        var sessions = new List<SessionSummary>(data.Sessions.Count);
        foreach (var dir in data.Sessions)
        {
            ct.ThrowIfCancellationRequested();
            sessions.Add(MergeSession(dir));
        }

        // design.md §3 稳定排序：日期降序 → 会话 ID 升序；无日期条目排末尾。
        sessions.Sort(static (a, b) =>
        {
            var byDate = CompareDateDesc(a.CreatedAt, b.CreatedAt);
            if (byDate != 0)
                return byDate;
            var byId = string.CompareOrdinal(a.SessionId ?? string.Empty, b.SessionId ?? string.Empty);
            if (byId != 0)
                return byId;
            return string.CompareOrdinal(
                a.Generations.Count > 0 ? a.Generations[0].FullPath : string.Empty,
                b.Generations.Count > 0 ? b.Generations[0].FullPath : string.Empty);
        });

        var total = sessions.Sum(static s => s.TotalBytes);
        return new InventoryResult(null, sessions, data.AnomalousFiles) { TotalBytes = total };
    }

    public Task<IReadOnlyList<SessionSummary>> ScanAsync(ResolvedRoots roots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var result = Scan(roots.SessionRoot, ct);
        return Task.FromResult(result.Sessions);
    }

    private static SessionSummary MergeSession(ScannedSessionDirectory dir)
    {
        var generations = dir.Files
            .Select(static f => f.Log)
            .OrderBy(static l => l.Generation)
            .ToArray();

        var totalBytes = generations.Sum(static l => l.Bytes);

        // 身份验证：全部有效 header 的 sessionId 必须一致，且与目录身份映射成立。
        var headerIds = dir.Files
            .Select(static f => f.HeaderSessionId)
            .Where(static id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var anyIdentityMismatch = dir.Files.Any(static f => f.IdentityMismatch);
        var isAmbiguous = dir.ConflictingGenerations.Count > 0
            || anyIdentityMismatch
            || headerIds.Length > 1;
        var sessionId = headerIds.Length == 1 && !anyIdentityMismatch ? headerIds[0] : null;

        var status = AggregateStatus(generations);

        // createdAt 仅取有效 header（design.md §3）；多代际取 numerically highest canonical generation。
        var createdAt = dir.Files
            .Where(static f => f.HeaderCreatedAt is not null)
            .OrderByDescending(static f => f.Log.Generation)
            .Select(static f => f.HeaderCreatedAt)
            .FirstOrDefault();

        // Project 取会话目录的上级目录名（相对 SessionRoot）。
        var project = ProjectOf(dir.RelativeDirectory);

        return new SessionSummary(
            sessionId,
            project,
            createdAt,
            LastActivityAt: null,
            createdAt is not null ? DateOrigin.Header : DateOrigin.Unknown,
            generations,
            status)
        {
            TotalBytes = totalBytes,
            IsAmbiguous = isAmbiguous,
            IsArchived = sessionId is not null && !isAmbiguous ? dir.IsArchived : null,
            Title = dir.Files.OrderByDescending(static f => f.Log.Generation).Select(static f => f.Title).FirstOrDefault(static t => !string.IsNullOrEmpty(t)),
        };
    }

    /// <summary>会话状态取成员文件最严重者：Unreadable &gt; Corrupt &gt; Unsupported &gt; Partial &gt; Valid。</summary>
    private static ParseStatus AggregateStatus(IReadOnlyList<PhysicalLog> generations)
    {
        var worst = ParseStatus.Valid;
        foreach (var log in generations)
        {
            if (Severity(log.Status) > Severity(worst))
                worst = log.Status;
        }

        return worst;

        static int Severity(ParseStatus status) => status switch
        {
            ParseStatus.Valid => 0,
            ParseStatus.Partial => 1,
            ParseStatus.Unsupported => 2,
            ParseStatus.Corrupt => 3,
            ParseStatus.Unreadable => 4,
            _ => 5,
        };
    }

    private static string? ProjectOf(string relativeDirectory)
    {
        var normalized = relativeDirectory.Replace('/', Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(normalized.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(parent))
            return null;
        var segments = parent.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : segments[0];
    }

    private static int CompareDateDesc(DateTimeOffset? a, DateTimeOffset? b)
    {
        // 无日期排末尾：null 视为最小（降序时排最后）。
        if (a is null && b is null)
            return 0;
        if (a is null)
            return 1;
        if (b is null)
            return -1;
        return b.Value.CompareTo(a.Value);
    }
}
