using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>清理目标类别（T-24 会话日志与专属缓存；T-27 专属缓存；T-28 桌面缓存/日志；附件归 T-29）。</summary>
public enum CleanupCategory
{
    SessionLog,
    DedicatedCache,
    DesktopCache,
    DesktopLog,
}

/// <summary>逐文件清理目标（design.md §1 草案）：Expected* 供执行前二次验证。</summary>
public sealed record CleanupTarget(
    string AbsolutePath,
    CleanupCategory Category,
    string? OwnerSessionId,
    long ExpectedLength,
    DateTimeOffset ExpectedWriteUtc,
    string? ExpectedSha256);

/// <summary>清理计划（design.md §1）：RootFingerprint 为规范根路径 SHA-256 小写 hex。</summary>
public sealed record CleanupPlan(
    Guid PlanId,
    DateTimeOffset CreatedUtc,
    string RootFingerprint,
    IReadOnlyList<CleanupTarget> Targets,
    bool RequiresBackup);

/// <summary>被排除会话及原因（未选/异常格式/身份冲突/未知文件/根外等），供 UI 明示。</summary>
public sealed record PlanExclusion(string? SessionId, string Reason, string? Detail);

/// <summary>规划结果：根不可用只给错误码；计划与排除明细互不混淆。</summary>
public sealed record CleanupPlanResult(
    string? RootErrorCode,
    CleanupPlan? Plan,
    IReadOnlyList<PlanExclusion> Exclusions)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// 专属投影缓存证据（由调用方从 SessionCacheIndex 的 Matched 条目转换；Application 不引用 Infrastructure）。
/// 仅精确对应（Matched）的缓存会作为输入出现；未选会话的缓存由规划器按选择集过滤。
/// </summary>
public sealed record DedicatedCacheEvidence(
    string FullPath,
    string OwnerSessionId,
    long ExpectedLength,
    DateTimeOffset ExpectedWriteUtc,
    string? ExpectedSha256);

/// <summary>
/// T-24 会话精确清理计划（TC-08/TC-11）：由已验证会话 ID 与用户选择集合产生逐文件计划，
/// 含该会话全部已识别 generation 与可验证的专属缓存。纯规划、零写入：
/// 未选会话、异常格式（Status≠Valid）、身份冲突（无 ID/重复 ID/Ambiguous/同文件多主）、
/// 未知文件（文件名白名单外）与根外路径（根内复验，同 T-21 语义）一律不进入计划。
/// 日期筛选只产生完整会话选择，绝不拆分会话内部。
/// </summary>
public sealed class CleanupPlanner
{
    private const string CodeTraversal = "PATH_TRAVERSAL";
    private const string CodeCrossRoot = "CROSS_ROOT";
    private const string CodeRootInvalid = "ROOT_INVALID";

    /// <summary>生成计划。<paramref name="selectedSessions"/> 为用户选择（含日期筛选结果）的完整会话集合。</summary>
    public CleanupPlanResult Build(
        string sessionRoot,
        IReadOnlyList<SessionSummary> selectedSessions,
        IReadOnlyList<DedicatedCacheEvidence> dedicatedCaches)
    {
        ArgumentNullException.ThrowIfNull(selectedSessions);
        ArgumentNullException.ThrowIfNull(dedicatedCaches);

        string rootFull;
        try
        {
            rootFull = NormalizeRoot(sessionRoot);
        }
        catch (ArgumentException)
        {
            return new CleanupPlanResult(CodeRootInvalid, null, []);
        }
        var exclusions = new List<PlanExclusion>();
        var targets = new List<CleanupTarget>();

        // 身份冲突预检：选择集内重复会话 ID、空 ID。
        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in selectedSessions)
        {
            if (session.SessionId is not null)
            {
                idCounts.TryGetValue(session.SessionId, out var n);
                idCounts[session.SessionId] = n + 1;
            }
        }

        // 同一物理文件被多个选择会话引用 → 同文件多主，全部涉事会话排除。
        var pathOwners = new Dictionary<string, List<SessionSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in selectedSessions)
        {
            foreach (var log in session.Generations)
            {
                if (!pathOwners.TryGetValue(log.FullPath, out var owners))
                {
                    owners = [];
                    pathOwners[log.FullPath] = owners;
                }

                owners.Add(session);
            }
        }

        var plannedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in selectedSessions)
        {
            if (session.SessionId is null)
            {
                exclusions.Add(new PlanExclusion(null, "UNVERIFIED_SESSION_ID", "会话 ID 未经 header 验证，不进入计划。"));
                continue;
            }

            if (idCounts[session.SessionId] > 1)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "DUPLICATE_SESSION_ID", "选择集内会话 ID 重复（身份冲突），全部排除。"));
                continue;
            }

            if (session.Status != ParseStatus.Valid)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "ABNORMAL_FORMAT", $"解析状态 {session.Status}，允许原始备份但不允许自动清理。"));
                continue;
            }

            if (session.IsAmbiguous)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "AMBIGUOUS", "同代双编码或 header 身份冲突，阻止清理。"));
                continue;
            }

            if (session.Generations.Count == 0)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "NO_GENERATIONS", "会话没有物理日志文件。"));
                continue;
            }

            var multiOwner = session.Generations.Any(log => pathOwners[log.FullPath].Count > 1);
            if (multiOwner)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "SHARED_FILE", "同一物理文件被多个会话引用（身份冲突）。"));
                continue;
            }

            if (!TryAddSessionTargets(session, rootFull, targets, exclusions))
            {
                continue;
            }

            plannedIds.Add(session.SessionId);
        }

        // 专属缓存：仅选择且已规划会话的 Matched 缓存进入；同样过根白名单复验。
        foreach (var cache in dedicatedCaches)
        {
            if (!plannedIds.Contains(cache.OwnerSessionId))
            {
                continue; // 未选会话（或其缓存属于被排除会话）：不进入计划。
            }

            if (TryContain(rootFull, cache.FullPath, out var cacheFull, out var cacheError))
            {
                targets.Add(new CleanupTarget(
                    cacheFull, CleanupCategory.DedicatedCache, cache.OwnerSessionId,
                    cache.ExpectedLength, cache.ExpectedWriteUtc, cache.ExpectedSha256));
            }
            else
            {
                exclusions.Add(new PlanExclusion(cache.OwnerSessionId, "CACHE_" + cacheError, cache.FullPath));
            }
        }

        targets.Sort(static (a, b) => string.CompareOrdinal(a.AbsolutePath, b.AbsolutePath));

        var plan = new CleanupPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ComputeRootFingerprint(rootFull),
            targets,
            RequiresBackup: targets.Count > 0);
        return new CleanupPlanResult(null, plan, exclusions);
    }

    /// <summary>逐 generation 复验：文件名白名单、状态、根内；任一不合格整会话排除（不拆分会话：全部通过才提交目标）。</summary>
    private bool TryAddSessionTargets(
        SessionSummary session,
        string rootFull,
        List<CleanupTarget> targets,
        List<PlanExclusion> exclusions)
    {
        var staged = new List<CleanupTarget>(session.Generations.Count);
        foreach (var log in session.Generations)
        {
            if (log.Status != ParseStatus.Valid)
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "ABNORMAL_GENERATION", $"代际文件状态 {log.Status}：{log.FullPath}"));
                return false;
            }

            if (!IsWhitelistedFileName(Path.GetFileName(log.FullPath)))
            {
                exclusions.Add(new PlanExclusion(session.SessionId, "UNKNOWN_FILE", $"文件名不在白名单：{Path.GetFileName(log.FullPath)}"));
                return false;
            }

            if (!TryContain(rootFull, log.FullPath, out var logFull, out var pathError))
            {
                exclusions.Add(new PlanExclusion(session.SessionId, pathError!, log.FullPath));
                return false;
            }

            staged.Add(new CleanupTarget(
                logFull, CleanupCategory.SessionLog, session.SessionId,
                log.Bytes, log.ModifiedAt, log.Sha256));
        }

        targets.AddRange(staged);
        return true;
    }

    /// <summary>design.md §2 白名单：session.jsonl / session.jsonl.zstd / session.vN.jsonl(.zstd)，N 正整数；严格小写。</summary>
    internal static bool IsWhitelistedFileName(string fileName)
    {
        const string prefix = "session";
        const string baseExt = ".jsonl";
        const string zstdExt = ".zstd";

        var name = fileName;
        if (name.EndsWith(zstdExt, StringComparison.Ordinal))
        {
            name = name[..^zstdExt.Length];
        }

        if (!name.EndsWith(baseExt, StringComparison.Ordinal))
        {
            return false;
        }

        name = name[..^baseExt.Length];
        if (name == prefix)
        {
            return true;
        }

        if (!name.StartsWith(prefix + ".v", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = name[(prefix.Length + 2)..];
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return digits != "0";
    }

    /// <summary>根规范化（同 T-21 BackupManifest）：必须为绝对路径，去尾分隔符。</summary>
    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(full))
        {
            throw new ArgumentException("root 必须是绝对路径", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>根内复验（同 T-21 语义）：原始路径任何 .. 段先拒（PATH_TRAVERSAL），根外/跨盘拒（CROSS_ROOT）。</summary>
    private static bool TryContain(string rootFull, string fullPath, out string normalizedFull, out string? errorCode)
    {
        errorCode = null;
        normalizedFull = fullPath;

        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..")
            {
                errorCode = CodeTraversal;
                return false;
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(fullPath);
        }
        catch (ArgumentException)
        {
            errorCode = CodeRootInvalid;
            return false;
        }

        var rel = Path.GetRelativePath(rootFull, full);
        if (Path.IsPathFullyQualified(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            errorCode = CodeCrossRoot;
            return false;
        }

        normalizedFull = full;
        return true;
    }

    /// <summary>根指纹：规范化根完整路径（UTF-8）SHA-256 小写 hex；同根稳定，异根不同。</summary>
    private static string ComputeRootFingerprint(string rootFull)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootFull));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
