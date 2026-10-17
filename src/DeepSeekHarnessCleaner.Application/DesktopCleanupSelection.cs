using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 桌面文件证据（Application 层输入记录；调用方从桌面根只读枚举后转换，Application 不依赖 Infrastructure）。
/// </summary>
public sealed record DesktopFileEvidence(
    string FullPath,
    long ExpectedLength,
    DateTimeOffset ExpectedWriteUtc,
    string? ExpectedSha256);

/// <summary>未进入桌面计划的候选及原因。</summary>
public sealed record DesktopExclusion(string FullPath, string Reason, string? Detail = null);

/// <summary>桌面清理计划结果（复用 CleanupPlan/CleanupTarget 契约，RequiresBackup=false：常规缓存/日志无强制 ZIP）。</summary>
public sealed record DesktopCleanupPlanResult(
    string? RootErrorCode,
    CleanupPlan? Plan,
    IReadOnlyList<DesktopExclusion> Exclusions)
{
    public bool RootValid => RootErrorCode is null;
}

/// <summary>
/// T-28 桌面缓存/日志清理计划（TC-13，design.md §6）：
/// 仅固定 5 个 cache 目录（Cache、Code Cache、GPUCache、DawnGraphiteCache、DawnWebGPUCache）内的普通文件
/// 与 logs 目录内的普通日志文件；日志支持按文件修改时间上界筛选（cache 不受日期筛选影响）；
/// Local Storage、Session Storage、Network 及其他桌面数据一律排除；
/// 误选根（根自身或白名单目录自身作为目标）被拒绝。纯内存零 IO。
/// 计划只列文件目标；“清空后目录”的目录删除由接线/执行层处理。
/// </summary>
public sealed class DesktopCleanupSelection
{
    /// <summary>design.md §6 固定桌面缓存目录白名单（与 Infrastructure PathGuard.DesktopCacheDirs 同名单）。</summary>
    public static readonly IReadOnlyList<string> DesktopCacheDirs =
        ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    public const string LogsDirName = "logs";

    public const string OutOfScopeReason = "OUT_OF_DESKTOP_SCOPE";
    public const string RootNotTargetReason = "DESKTOP_ROOT_NOT_TARGET";
    public const string TopLevelNotTargetReason = "DESKTOP_TOPLEVEL_NOT_TARGET";
    public const string LogDateFilteredReason = "LOG_DATE_FILTERED";
    public const string DuplicateReason = "DESKTOP_DUPLICATE_PATH";

    /// <summary>
    /// <paramref name="logModifiedBefore"/>：仅纳入修改时间严格早于该时刻的日志（cache 不受影响）；null 表示不按日期筛选。
    /// </summary>
    public DesktopCleanupPlanResult Select(
        string desktopRoot,
        IReadOnlyList<DesktopFileEvidence> candidates,
        DateTimeOffset? logModifiedBefore = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        string rootFull;
        try
        {
            rootFull = NormalizeRoot(desktopRoot);
        }
        catch (ArgumentException)
        {
            return new DesktopCleanupPlanResult("ROOT_INVALID", null, []);
        }

        var exclusions = new List<DesktopExclusion>();
        var staged = new List<CleanupTarget>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var containError = TryContain(rootFull, candidate.FullPath, out var full);
            if (containError is not null)
            {
                exclusions.Add(new DesktopExclusion(candidate.FullPath, "DESKTOP_" + containError));
                continue;
            }

            var relative = Path.GetRelativePath(rootFull, full);
            if (relative == ".")
            {
                exclusions.Add(new DesktopExclusion(full, RootNotTargetReason));
                continue;
            }

            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length < 2)
            {
                // 根下直接条目：白名单目录自身（目录不是删除目标）或根下散落文件（不在任何白名单目录内）。
                exclusions.Add(new DesktopExclusion(full, TopLevelNotTargetReason, relative));
                continue;
            }

            var topSegment = segments[0];
            CleanupCategory category;
            if (DesktopCacheDirs.Any(d => string.Equals(d, topSegment, StringComparison.OrdinalIgnoreCase)))
            {
                category = CleanupCategory.DesktopCache;
            }
            else if (string.Equals(topSegment, LogsDirName, StringComparison.OrdinalIgnoreCase))
            {
                category = CleanupCategory.DesktopLog;
            }
            else
            {
                // Local Storage、Session Storage、Network 及其他桌面数据：所有组合下不列入。
                exclusions.Add(new DesktopExclusion(full, OutOfScopeReason, relative));
                continue;
            }

            if (category == CleanupCategory.DesktopLog
                && logModifiedBefore is not null
                && candidate.ExpectedWriteUtc >= logModifiedBefore)
            {
                exclusions.Add(new DesktopExclusion(full, LogDateFilteredReason));
                continue;
            }

            if (!seenPaths.Add(full))
            {
                exclusions.Add(new DesktopExclusion(full, DuplicateReason));
                continue;
            }

            staged.Add(new CleanupTarget(
                full,
                category,
                OwnerSessionId: null,
                candidate.ExpectedLength,
                candidate.ExpectedWriteUtc,
                candidate.ExpectedSha256));
        }

        staged.Sort(static (a, b) => string.CompareOrdinal(a.AbsolutePath, b.AbsolutePath));

        var plan = new CleanupPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ComputeRootFingerprint(rootFull),
            staged,
            RequiresBackup: false);

        return new DesktopCleanupPlanResult(null, plan, exclusions);
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
            // 空/相对或驱动器根、UNC 共享根：不得作为桌面根。
            throw new ArgumentException("根不得为驱动器根", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>与 T-24/T-27 同构的根内复验。</summary>
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
