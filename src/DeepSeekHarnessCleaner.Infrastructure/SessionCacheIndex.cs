using System.Security;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>专属投影缓存与会话的映射判定（design.md §6：仅精确对应且无同名冲突才可确认）。</summary>
public enum SessionCacheStatus
{
    /// <summary>session_projcache/sessions/&lt;ID&gt;.json 与会话清单中的已知会话 ID 精确对应。</summary>
    Matched,

    /// <summary>命名合规（&lt;id&gt;.json）但 ID 不在已知会话清单中：确认不属于任何已知会话。</summary>
    Unmatched,

    /// <summary>无法确认映射（结构不一致、reparse、命名冲突或缺少清单）：跳过并提示，不修改。</summary>
    Unknown,
}

/// <summary>单条专属投影缓存的索引结果；只读，不含任何删除动作。</summary>
public sealed record SessionCacheEntry(
    string FullPath,
    string RelativePath,
    string? SessionId,
    SessionCacheStatus Status,
    string? Reason);

/// <summary>全量缓存候选（session_projcache/ 目录与 session_projcache.json），仅供 T-27 显式勾选。</summary>
public sealed record WholeCacheEntry(
    string FullPath,
    string RelativePath,
    bool IsDirectory,
    long Length);

/// <summary>投影缓存只读索引结果。</summary>
public sealed record CacheIndexResult(
    string? RootErrorCode,
    IReadOnlyList<SessionCacheEntry> DedicatedCaches,
    IReadOnlyList<WholeCacheEntry> WholeCacheCandidates,
    IReadOnlyList<DiscoveryAnomaly> Anomalies)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// T-16：按 &lt;id&gt;.json 命名与目录布局关联单会话专属投影缓存，只建只读索引。
/// 不创建、写入或删除任何文件；workspace.json 不在索引范围，永不被列出或触碰。
/// </summary>
public sealed class SessionCacheIndex
{
    public const string ProjCacheDirName = "session_projcache";
    public const string SessionsDirName = "sessions";
    public const string ProjCacheJsonName = "session_projcache.json";
    public const string CacheExtension = ".json";

    public const string StructureMismatchReason = "CACHE_STRUCTURE_MISMATCH";
    public const string NameCollisionReason = "CACHE_NAME_COLLISION";
    public const string NoInventoryReason = "CACHE_INVENTORY_MISSING";
    public const string NotInInventoryReason = "CACHE_ID_NOT_IN_INVENTORY";
    public const string ReparseReason = PathGuard.PathReparseErrorCode;
    public const string FileAccessReason = PathGuard.FileAccessDeniedErrorCode;

    private readonly PathGuard _guard;

    public SessionCacheIndex(PathGuard guard) =>
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));

    /// <summary>只读扫描 storagesRoot 下的投影缓存布局，并与已知会话 ID 集合比对。</summary>
    public CacheIndexResult Build(string storagesRoot, IReadOnlyList<string> knownSessionIds)
    {
        ArgumentNullException.ThrowIfNull(knownSessionIds);

        // 与 SessionLogDiscovery 相同的根有效性探测：PATH_ESCAPE 表示根合法。
        var probe = _guard.Check(storagesRoot, storagesRoot);
        if (probe.ErrorCode is null || probe.ErrorCode != PathGuard.PathEscapeErrorCode)
            return new CacheIndexResult(probe.ErrorCode ?? PathGuard.RootInvalidErrorCode, [], [], []);

        var rootFull = probe.FullPath;
        var dedicated = new List<SessionCacheEntry>();
        var whole = new List<WholeCacheEntry>();
        var anomalies = new List<DiscoveryAnomaly>();

        var cacheDirFull = Path.Combine(rootFull, ProjCacheDirName);
        var (cacheAttrs, cacheError) = TryAttributes(cacheDirFull);
        if (cacheError is not null)
            anomalies.Add(new DiscoveryAnomaly(cacheDirFull, ProjCacheDirName, cacheError));
        else if (cacheAttrs is not null)
        {
            if ((cacheAttrs.Value & FileAttributes.ReparsePoint) != 0)
            {
                anomalies.Add(new DiscoveryAnomaly(cacheDirFull, ProjCacheDirName, ReparseReason));
            }
            else if ((cacheAttrs.Value & FileAttributes.Directory) != 0)
            {
                // 全量缓存目录候选：仅记录，不枚举其内容（T-27 显式勾选时才展开）。
                whole.Add(new WholeCacheEntry(cacheDirFull, ProjCacheDirName, true, 0));
                ScanSessionsDir(rootFull, cacheDirFull, knownSessionIds, dedicated, anomalies);
            }
            else
            {
                // session_projcache 不是目录：结构不一致，跳过。
                anomalies.Add(new DiscoveryAnomaly(cacheDirFull, ProjCacheDirName, StructureMismatchReason));
            }
        }

        var cacheJsonFull = Path.Combine(rootFull, ProjCacheJsonName);
        var (jsonAttrs, jsonError) = TryAttributes(cacheJsonFull);
        if (jsonError is not null)
            anomalies.Add(new DiscoveryAnomaly(cacheJsonFull, ProjCacheJsonName, jsonError));
        else if (jsonAttrs is not null)
        {
            if ((jsonAttrs.Value & FileAttributes.ReparsePoint) != 0)
                anomalies.Add(new DiscoveryAnomaly(cacheJsonFull, ProjCacheJsonName, ReparseReason));
            else if ((jsonAttrs.Value & FileAttributes.Directory) != 0)
                anomalies.Add(new DiscoveryAnomaly(cacheJsonFull, ProjCacheJsonName, StructureMismatchReason));
            else
                whole.Add(new WholeCacheEntry(cacheJsonFull, ProjCacheJsonName, false, TryLength(cacheJsonFull, cacheJsonFull, anomalies)));
        }

        dedicated.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        whole.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        anomalies.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        return new CacheIndexResult(null, dedicated, whole, anomalies);
    }

    private void ScanSessionsDir(
        string rootFull,
        string cacheDirFull,
        IReadOnlyList<string> knownSessionIds,
        List<SessionCacheEntry> dedicated,
        List<DiscoveryAnomaly> anomalies)
    {
        var sessionsFull = Path.Combine(cacheDirFull, SessionsDirName);
        var (sessionsAttrs, sessionsError) = TryAttributes(sessionsFull);
        if (sessionsError is not null)
        {
            anomalies.Add(new DiscoveryAnomaly(sessionsFull, Relative(rootFull, sessionsFull), sessionsError));
            return;
        }

        if (sessionsAttrs is null)
            return; // 布局不存在：无专属缓存，也不算异常。
        if ((sessionsAttrs.Value & FileAttributes.ReparsePoint) != 0)
        {
            anomalies.Add(new DiscoveryAnomaly(sessionsFull, Relative(rootFull, sessionsFull), ReparseReason));
            return;
        }
        if ((sessionsAttrs.Value & FileAttributes.Directory) == 0)
        {
            anomalies.Add(new DiscoveryAnomaly(sessionsFull, Relative(rootFull, sessionsFull), StructureMismatchReason));
            return;
        }

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(sessionsFull).EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            anomalies.Add(new DiscoveryAnomaly(sessionsFull, Relative(rootFull, sessionsFull), FileAccessReason));
            return;
        }

        // 第一遍：结构判定（reparse/目录/非 .json/空 ID），合规项进入候选。
        var candidates = new List<(FileSystemInfo Info, string Name, string Id, string Relative)>();
        foreach (var entry in entries)
        {
            var relative = Relative(rootFull, entry.FullName) ?? string.Empty;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                dedicated.Add(new SessionCacheEntry(entry.FullName, relative, null, SessionCacheStatus.Unknown, ReparseReason));
                continue;
            }
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                dedicated.Add(new SessionCacheEntry(entry.FullName, relative, null, SessionCacheStatus.Unknown, StructureMismatchReason));
                continue;
            }

            var check = _guard.Check(rootFull, entry.FullName);
            if (!check.IsAllowed)
            {
                dedicated.Add(new SessionCacheEntry(
                    entry.FullName, relative, null, SessionCacheStatus.Unknown, check.ErrorCode ?? FileAccessReason));
                continue;
            }

            var name = entry.Name;
            if (!name.EndsWith(CacheExtension, StringComparison.Ordinal)
                || name.Length <= CacheExtension.Length)
            {
                dedicated.Add(new SessionCacheEntry(entry.FullName, relative, null, SessionCacheStatus.Unknown, StructureMismatchReason));
                continue;
            }

            candidates.Add((entry, name, name[..^CacheExtension.Length], check.RelativePath!));
        }

        // 第二遍：同名冲突检测（大小写不敏感文件系统上的防御性检查），再做映射判定。
        var known = new HashSet<string>(knownSessionIds, StringComparer.Ordinal);
        var collisions = candidates
            .GroupBy(c => c.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        foreach (var candidate in candidates)
        {
            if (collisions.Contains(candidate))
            {
                dedicated.Add(new SessionCacheEntry(candidate.Info.FullName, candidate.Relative, null, SessionCacheStatus.Unknown, NameCollisionReason));
            }
            else if (known.Count == 0)
            {
                dedicated.Add(new SessionCacheEntry(candidate.Info.FullName, candidate.Relative, null, SessionCacheStatus.Unknown, NoInventoryReason));
            }
            else if (known.Contains(candidate.Id))
            {
                dedicated.Add(new SessionCacheEntry(candidate.Info.FullName, candidate.Relative, candidate.Id, SessionCacheStatus.Matched, null));
            }
            else
            {
                dedicated.Add(new SessionCacheEntry(candidate.Info.FullName, candidate.Relative, null, SessionCacheStatus.Unmatched, NotInInventoryReason));
            }
        }
    }

    private long TryLength(string path, string anomalyPath, List<DiscoveryAnomaly> anomalies)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            anomalies.Add(new DiscoveryAnomaly(anomalyPath, Path.GetFileName(anomalyPath), FileAccessReason));
            return 0;
        }
    }

    private static (FileAttributes? Attributes, string? Error) TryAttributes(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs, null);
        }
        catch (FileNotFoundException)
        {
            return (null, null); // 缺失：不是异常，交由调用方按缺失处理。
        }
        catch (DirectoryNotFoundException)
        {
            return (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return (null, ex is ArgumentException ? PathGuard.RootInvalidErrorCode : FileAccessReason);
        }
    }

    private static string? Relative(string rootFull, string path)
    {
        try
        {
            return Path.GetRelativePath(rootFull, path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
