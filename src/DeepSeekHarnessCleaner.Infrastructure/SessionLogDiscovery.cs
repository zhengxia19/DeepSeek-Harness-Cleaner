using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessCleaner.Infrastructure;

public enum SessionFileEncoding
{
    Plain,
    Zstd,
}

/// <param name="RelativePath">相对 SessionRoot 的路径（仅白名单文件）。</param>
/// <param name="Generation">代际：session.jsonl(.zstd) 为 0，session.vN.* 为 N。</param>
public sealed record SessionFileEntry(
    string FullPath,
    string RelativePath,
    int Generation,
    SessionFileEncoding Encoding,
    long Length);

/// <param name="ConflictingGenerations">同一目录内同代际同时存在明文与 zstd（design.md §2 Ambiguous，阻止清理）。</param>
public sealed record SessionCandidate(
    string RelativeDirectory,
    string FullPath,
    IReadOnlyList<SessionFileEntry> Files,
    IReadOnlyList<int> ConflictingGenerations)
{
    public bool IsAmbiguous => ConflictingGenerations.Count > 0;
}

/// <param name="Reason">异常原因码：NOT_WHITELISTED / UNEXPECTED_LOCATION / PATH_REPARSE / FILE_ACCESS_DENIED / MISSING。</param>
public sealed record DiscoveryAnomaly(string FullPath, string? RelativePath, string Reason);

/// <param name="RootErrorCode">根不可用时非空（ROOT_INVALID/PATH_REPARSE/FILE_ACCESS_DENIED），此时不扫描。</param>
public sealed record DiscoveryResult(
    string? RootErrorCode,
    IReadOnlyList<SessionCandidate> Candidates,
    IReadOnlyList<DiscoveryAnomaly> Anomalies)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// 会话物理候选发现（design.md §2，T-08）。只读扫描：不创建、写入或删除任何路径。
/// 规范命名文件按目录归组为候选（多代际=同一会话）；提取 generation/encoding；
/// 非白名单、根下直放、reparse 目录、无法访问均列入异常；同代双编码标记冲突。
/// </summary>
public sealed class SessionLogDiscovery
{
    public const string NotWhitelistedReason = "NOT_WHITELISTED";
    public const string UnexpectedLocationReason = "UNEXPECTED_LOCATION";
    public const string ReparseReason = PathGuard.PathReparseErrorCode;
    public const string FileAccessReason = PathGuard.FileAccessDeniedErrorCode;
    public const string MissingReason = "MISSING";

    private static readonly Regex GenerationPattern =
        new(@"^session(?:\.v([1-9][0-9]*))?\.jsonl(?:\.zstd)?$", RegexOptions.Compiled);

    private readonly PathGuard _guard;

    public SessionLogDiscovery(PathGuard guard) =>
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));

    public DiscoveryResult Discover(string sessionRoot)
    {
        // 以根自身为目标探测根有效性：PATH_ESCAPE 表示根合法（仅“根目录本身不是候选”被拒）。
        var probe = _guard.Check(sessionRoot, sessionRoot);
        if (probe.ErrorCode is null || probe.ErrorCode != PathGuard.PathEscapeErrorCode)
            return new DiscoveryResult(probe.ErrorCode ?? PathGuard.RootInvalidErrorCode, [], []);

        var rootFull = probe.FullPath;
        var candidates = new List<SessionCandidate>();
        var anomalies = new List<DiscoveryAnomaly>();
        Walk(rootFull, rootFull, candidates, anomalies);
        candidates.Sort(static (a, b) => string.CompareOrdinal(a.RelativeDirectory, b.RelativeDirectory));
        return new DiscoveryResult(null, candidates, anomalies);
    }

    private void Walk(
        string rootFull,
        string dirFull,
        List<SessionCandidate> candidates,
        List<DiscoveryAnomaly> anomalies)
    {
        var entries = new List<SessionFileEntry>();

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(dirFull).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            anomalies.Add(new DiscoveryAnomaly(dirFull, Relative(rootFull, dirFull), FileAccessReason));
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var check = _guard.Check(rootFull, file);
            if (!check.IsAllowed)
            {
                anomalies.Add(new DiscoveryAnomaly(file, null, check.ErrorCode ?? MissingReason));
                continue;
            }

            if (!_guard.IsSessionFileName(name))
            {
                anomalies.Add(new DiscoveryAnomaly(file, check.RelativePath, NotWhitelistedReason));
                continue;
            }

            // 白名单名直接位于根下：不在会话目录内，属不规则位置。
            if (string.IsNullOrEmpty(check.RelativePath)
                || !check.RelativePath!.Contains(Path.DirectorySeparatorChar))
            {
                anomalies.Add(new DiscoveryAnomaly(file, check.RelativePath, UnexpectedLocationReason));
                continue;
            }

            var match = GenerationPattern.Match(name);
            if (!match.Success
                || (match.Groups[1].Success
                    && !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                anomalies.Add(new DiscoveryAnomaly(file, check.RelativePath, NotWhitelistedReason));
                continue;
            }

            var generation = match.Groups[1].Success
                ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
            var encoding = name.EndsWith(".zstd", StringComparison.Ordinal)
                ? SessionFileEncoding.Zstd
                : SessionFileEncoding.Plain;

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                anomalies.Add(new DiscoveryAnomaly(file, check.RelativePath, FileAccessReason));
                continue;
            }

            entries.Add(new SessionFileEntry(file, check.RelativePath!, generation, encoding, length));
        }

        if (entries.Count > 0)
        {
            var conflicts = entries
                .GroupBy(static e => e.Generation)
                .Where(static g => g.Select(static e => e.Encoding).Distinct().Count() > 1)
                .Select(static g => g.Key)
                .OrderBy(static k => k)
                .ToArray();
            var first = entries[0];
            candidates.Add(new SessionCandidate(
                Path.GetDirectoryName(first.RelativePath)!,
                Path.GetDirectoryName(first.FullPath)!,
                entries,
                conflicts));
        }

        string[] subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dirFull).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            anomalies.Add(new DiscoveryAnomaly(dirFull, Relative(rootFull, dirFull), FileAccessReason));
            return;
        }

        foreach (var sub in subDirs)
        {
            FileAttributes attrs;
            try
            {
                attrs = new DirectoryInfo(sub).Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                anomalies.Add(new DiscoveryAnomaly(sub, Relative(rootFull, sub), FileAccessReason));
                continue;
            }

            // 不进入 reparse 目录（design.md §2：不跟随 junction/symbolic link）。
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                anomalies.Add(new DiscoveryAnomaly(sub, Relative(rootFull, sub), ReparseReason));
                continue;
            }

            Walk(rootFull, sub, candidates, anomalies);
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
