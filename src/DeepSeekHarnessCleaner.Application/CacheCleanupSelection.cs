using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>投影缓存清理模式：全量必须显式选择 WholeCache，绝不隐式扩大范围（design.md §6）。</summary>
public enum CacheCleanupMode
{
    /// <summary>仅删除所选会话的精确匹配专属缓存 session_projcache/sessions/&lt;ID&gt;.json。</summary>
    SingleSession,

    /// <summary>用户显式勾选的全量：session_projcache/ 内经验证普通文件 + session_projcache.json。</summary>
    WholeCache,
}

/// <summary>
/// 缓存文件证据（Application 层输入记录；调用方从 T-16 SessionCacheIndex 条目转换，
/// 保持 Application 不依赖 Infrastructure）。
/// </summary>
public sealed record CacheFileEvidence(
    string FullPath,
    string? OwnerSessionId,
    long ExpectedLength,
    DateTimeOffset ExpectedWriteUtc,
    string? ExpectedSha256);

/// <summary>未进入计划的缓存候选及原因。</summary>
public sealed record CacheExclusion(string FullPath, string Reason, string? Detail = null);

/// <summary>缓存清理计划结果（与 T-24 会话计划同用 CleanupPlan/CleanupTarget 契约）。</summary>
public sealed record CacheCleanupPlanResult(
    string? RootErrorCode,
    CleanupPlan? Plan,
    IReadOnlyList<CacheExclusion> Exclusions)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// T-27 会话投影缓存独立清理计划（TC-12，design.md §6）：
/// 单会话仅精确匹配（&lt;ID&gt;.json 与会话 ID 严格一致且位于 session_projcache/sessions/）；
/// 全量仅显式 WholeCache 模式；workspace.json 永不列入；其他存储域跳过；
/// 根外/穿越复验同 T-24。缓存计划 RequiresBackup=false（缓存删除不要求会话 ZIP）。
/// 纯内存零 IO。
/// </summary>
public sealed class CacheCleanupSelection
{
    public const string ProjCacheDirName = "session_projcache";
    public const string SessionsDirName = "sessions";
    public const string ProjCacheJsonName = "session_projcache.json";
    public const string WorkspaceJsonName = "workspace.json";

    public const string WorkspaceReason = "WORKSPACE_NEVER_CLEANED";
    public const string OutOfScopeReason = "OUT_OF_CACHE_SCOPE";
    public const string StructureMismatchReason = "CACHE_STRUCTURE_MISMATCH";
    public const string NotSelectedReason = "CACHE_NOT_SELECTED";
    public const string DuplicateReason = "CACHE_DUPLICATE_PATH";

    public CacheCleanupPlanResult Select(
        string storagesRoot,
        CacheCleanupMode mode,
        IReadOnlyList<string> selectedSessionIds,
        IReadOnlyList<CacheFileEvidence> candidates)
    {
        ArgumentNullException.ThrowIfNull(selectedSessionIds);
        ArgumentNullException.ThrowIfNull(candidates);

        string rootFull;
        try
        {
            rootFull = NormalizeRoot(storagesRoot);
        }
        catch (ArgumentException)
        {
            return new CacheCleanupPlanResult("ROOT_INVALID", null, []);
        }

        var selected = new HashSet<string>(selectedSessionIds, StringComparer.Ordinal);
        var exclusions = new List<CacheExclusion>();
        var staged = new List<CleanupTarget>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var containError = TryContain(rootFull, candidate.FullPath, out var full);
            if (containError is not null)
            {
                exclusions.Add(new CacheExclusion(candidate.FullPath, "CACHE_" + containError));
                continue;
            }

            var fileName = Path.GetFileName(full);
            if (string.Equals(fileName, WorkspaceJsonName, StringComparison.OrdinalIgnoreCase))
            {
                exclusions.Add(new CacheExclusion(full, WorkspaceReason));
                continue;
            }

            if (!seenPaths.Add(full))
            {
                exclusions.Add(new CacheExclusion(full, DuplicateReason));
                continue;
            }

            var relative = Path.GetRelativePath(rootFull, full);

            if (mode == CacheCleanupMode.WholeCache)
            {
                if (!IsWithinWholeCacheScope(relative))
                {
                    exclusions.Add(new CacheExclusion(full, OutOfScopeReason, relative));
                    continue;
                }

                staged.Add(NewTarget(full, candidate));
                continue;
            }

            // SingleSession：仅精确对应所选会话 ID 的专属缓存。
            if (candidate.OwnerSessionId is null || !selected.Contains(candidate.OwnerSessionId))
            {
                exclusions.Add(new CacheExclusion(full, NotSelectedReason));
                continue;
            }

            var expectedRelative = Path.Combine(ProjCacheDirName, SessionsDirName, candidate.OwnerSessionId + ".json");
            if (!string.Equals(relative, expectedRelative, StringComparison.Ordinal))
            {
                exclusions.Add(new CacheExclusion(full, StructureMismatchReason, relative));
                continue;
            }

            staged.Add(NewTarget(full, candidate));
        }

        staged.Sort(static (a, b) => string.CompareOrdinal(a.AbsolutePath, b.AbsolutePath));

        var plan = new CleanupPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ComputeRootFingerprint(rootFull),
            staged,
            RequiresBackup: false);

        return new CacheCleanupPlanResult(null, plan, exclusions);
    }

    /// <summary>全量范围：session_projcache.json（根下）或 session_projcache\ 内任意深度；其余存储域跳过。</summary>
    private static bool IsWithinWholeCacheScope(string relative)
    {
        if (string.Equals(relative, ProjCacheJsonName, StringComparison.Ordinal))
        {
            return true;
        }

        var dirPrefix = ProjCacheDirName + Path.DirectorySeparatorChar;
        return relative.StartsWith(dirPrefix, StringComparison.Ordinal)
            && relative.Length > dirPrefix.Length;
    }

    private static CleanupTarget NewTarget(string full, CacheFileEvidence candidate) =>
        new(full, CleanupCategory.DedicatedCache, candidate.OwnerSessionId,
            candidate.ExpectedLength, candidate.ExpectedWriteUtc, candidate.ExpectedSha256);

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("根路径为空", nameof(root));
        }

        var full = Path.GetFullPath(root.Trim());
        if (Path.GetPathRoot(full) is not { Length: > 1 })
        {
            throw new ArgumentException("根不得为驱动器根", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>与 T-24 CleanupPlanner 同构的根内复验（Application 不依赖 Infrastructure PathGuard）。</summary>
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
