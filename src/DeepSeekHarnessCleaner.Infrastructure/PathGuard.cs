using System.Text.RegularExpressions;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>路径检查结论（interfaces.md §5：PATH_REPARSE/PATH_ESCAPE 拒绝目标）。</summary>
public enum PathCheckStatus
{
    Allowed,
    /// <summary>目标或中间目录不存在（含被重命名）：不得删除。</summary>
    Missing,
    Rejected,
}

/// <param name="FullPath">规范化后的目标绝对路径。</param>
/// <param name="RelativePath">仅 Allowed 时非空：相对受控根的路径。</param>
public sealed record PathCheckResult(
    string FullPath,
    string? RelativePath,
    PathCheckStatus Status,
    string? ErrorCode)
{
    public bool IsAllowed => Status == PathCheckStatus.Allowed;
}

/// <summary>
/// 路径白名单验证（design.md §7、architecture.md §5）。
/// 根包含校验、逐段 reparse 检查、UNC/设备路径/ADS/驱动器根/用户目录根/项目目录拒绝。
/// 只读检查：不创建、写入或删除任何路径。
/// </summary>
public sealed class PathGuard
{
    public const string RootInvalidErrorCode = "ROOT_INVALID";
    public const string PathReparseErrorCode = "PATH_REPARSE";
    public const string PathEscapeErrorCode = "PATH_ESCAPE";
    public const string FileAccessDeniedErrorCode = "FILE_ACCESS_DENIED";

    /// <summary>桌面缓存目录白名单（design.md §6，不含 logs/Local Storage/Session Storage/Network）。</summary>
    public static readonly string[] DesktopCacheDirs =
        ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    /// <summary>会话文件名白名单：session.jsonl(.zstd)、session.vN.jsonl(.zstd)，N 为正整数（design.md §3）。大小写敏感。</summary>
    private static readonly Regex SessionFileNamePattern =
        new(@"^session(\.v[1-9][0-9]*)?\.jsonl(\.zstd)?$", RegexOptions.Compiled);

    public bool IsSessionFileName(string fileName) => SessionFileNamePattern.IsMatch(fileName);

    /// <summary>相对根的首段是否属于桌面缓存目录白名单。</summary>
    public bool IsDesktopCacheDirectory(string relativeDir)
    {
        var head = relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return DesktopCacheDirs.Contains(head, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验 target 是否可作为 root 内的受控删除候选。
    /// 拒绝：字面 <c>..</c> 段、根外/跨根、根目录本身、UNC/设备路径、ADS、
    /// 根到目标每一级的 reparse point、目录目标（除非 allowDirectory）。
    /// </summary>
    public PathCheckResult Check(string root, string target, bool allowDirectory = false)
    {
        var (rootFull, rootError) = Normalize(root);
        if (rootError is not null)
            return new PathCheckResult(root, null, PathCheckStatus.Rejected, RootInvalidErrorCode);
        var rootProblem = ValidateRoot(rootFull!);
        if (rootProblem is not null)
            return new PathCheckResult(rootFull!, null, PathCheckStatus.Rejected, rootProblem);

        var (targetFull, targetError) = Normalize(target);
        if (targetError is not null
            || HasDotDotSegment(target)
            || IsUncOrDevice(targetFull!)
            || HasAdsColon(targetFull!))
            return new PathCheckResult(targetFull ?? target, null, PathCheckStatus.Rejected, PathEscapeErrorCode);

        // 根目录本身不是删除候选（design.md §7.1 拒绝根目录）。
        if (string.Equals(targetFull, rootFull, PathComparison))
            return new PathCheckResult(targetFull!, null, PathCheckStatus.Rejected, PathEscapeErrorCode);

        var relative = Path.GetRelativePath(rootFull!, targetFull!);
        if (relative == "."
            || Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new PathCheckResult(targetFull!, null, PathCheckStatus.Rejected, PathEscapeErrorCode);

        // 从根到目标逐段检查 reparse（design.md §7.2：GetFullPath 不能消除软链风险）。
        var segments = relative.Split(Path.DirectorySeparatorChar);
        var prefix = rootFull!;
        FileAttributes? targetAttrs = null;
        for (var i = 0; i <= segments.Length; i++)
        {
            if (i > 0)
                prefix = Path.Combine(prefix, segments[i - 1]);

            var (attrs, error) = TryAttributes(prefix);
            if (error is not null)
                return new PathCheckResult(targetFull!, null, PathCheckStatus.Rejected, error);
            if (attrs is null)
                return new PathCheckResult(targetFull!, null, PathCheckStatus.Missing, null); // 缺失/被重命名
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return new PathCheckResult(targetFull!, null, PathCheckStatus.Rejected, PathReparseErrorCode);
            if (i == segments.Length)
                targetAttrs = attrs;
        }

        if (!allowDirectory && (targetAttrs!.Value & FileAttributes.Directory) != 0)
            return new PathCheckResult(targetFull!, null, PathCheckStatus.Rejected, PathEscapeErrorCode);

        return new PathCheckResult(targetFull!, relative, PathCheckStatus.Allowed, null);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static (string? Full, string? Error) Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, RootInvalidErrorCode);
        try
        {
            return (Path.GetFullPath(path), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (null, RootInvalidErrorCode);
        }
    }

    /// <summary>受控根自身校验：拒绝驱动器根、用户目录根、项目目录、UNC/设备/ADS、根上的 reparse。</summary>
    private static string? ValidateRoot(string rootFull)
    {
        if (HasDotDotSegment(rootFull) || IsUncOrDevice(rootFull) || HasAdsColon(rootFull))
            return RootInvalidErrorCode;

        var rootOfRoot = Path.GetPathRoot(rootFull);
        if (rootOfRoot is not null && string.Equals(rootFull.TrimEnd(Path.DirectorySeparatorChar),
                rootOfRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            return RootInvalidErrorCode; // 驱动器根

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile)
            && string.Equals(rootFull.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            return RootInvalidErrorCode; // 用户目录根

        var projectDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var insideProject = Path.GetRelativePath(projectDir, rootFull);
        if (!Path.IsPathRooted(insideProject) && insideProject != ".."
            && !insideProject.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return RootInvalidErrorCode; // 项目源目录及其内部

        var (attrs, error) = TryAttributes(rootFull);
        if (error is not null)
            return error;
        if (attrs is null)
            return RootInvalidErrorCode; // 根不存在：不得隐式创建
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            return PathReparseErrorCode;
        return null;
    }

    private static bool HasDotDotSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => segment == "..");

    private static bool IsUncOrDevice(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool HasAdsColon(string path) => path.Length > 2 && path.IndexOf(':', 2) >= 0;

    private static (FileAttributes? Attrs, string? Error) TryAttributes(string path)
    {
        try
        {
            return (File.GetAttributes(path), null);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法可靠检查 → fail closed（TC-15：无法检查必须拒绝）。
            return (null, FileAccessDeniedErrorCode);
        }
    }
}
