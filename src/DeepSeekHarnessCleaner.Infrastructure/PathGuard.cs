using System.Text.RegularExpressions;
using DeepSeekHarnessCleaner.Application;

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
    public const string RootInvalidErrorCode = ErrorCodes.RootInvalid;
    public const string PathReparseErrorCode = ErrorCodes.PathReparse;
    public const string PathEscapeErrorCode = ErrorCodes.PathEscape;
    public const string FileAccessDeniedErrorCode = ErrorCodes.FileAccessDenied;

    /// <summary>会话文件名白名单：session.jsonl(.zstd)、session.vN.jsonl(.zstd)，N 为正整数（design.md §3）。大小写敏感。单一实现：白名单判定与代际解析共用此正则（S05）。</summary>
    private static readonly Regex SessionFileNamePattern =
        new(@"^session(?:\.v(?<generation>[1-9][0-9]*))?\.jsonl(?:\.zstd)?$", RegexOptions.Compiled);

    /// <summary>一次匹配同时完成白名单判定与代际捕获：Success=false 即白名单外；generation 组未命中即 v0 默认代际。</summary>
    public Match MatchSessionFileName(string fileName) => SessionFileNamePattern.Match(fileName);

    /// <summary>相对根的首段是否属于桌面缓存目录白名单。名单与比较语义取自 Application DesktopCleanupSelection（B06 单一实现；依赖方向 Infrastructure→Application）。</summary>
    public bool IsDesktopCacheDirectory(string relativeDir)
    {
        var head = relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return DesktopCleanupSelection.IsDesktopCacheTopSegment(head);
    }

    /// <summary>
    /// 祖先 reparse 走查（design.md §7.2，B03 单一实现）：自卷根向下逐段走查到 path，includeSelf 决定是否含 path 本身。
    /// Check 的走查仅覆盖根→目标内部，ValidateRoot 仅查根自身，均不含根之上的祖先，故此处独立实现。
    /// skipMissing=true 跳过不存在条目继续（尚未创建的路径）；否则 IO 失败原样抛出，由调用方按各自返回形态归类。
    /// 返回首个 reparse point 的完整路径；无则 null。
    /// </summary>
    public static string? FindReparseAncestor(string path, bool includeSelf, bool skipMissing)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetPathRoot(full);
        if (prefix is null)
            return null;
        if (IsReparsePath(prefix, skipMissing))
            return prefix;
        var segments = full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length && (includeSelf || i < segments.Length - 1); i++)
        {
            prefix = Path.Combine(prefix, segments[i]);
            if (IsReparsePath(prefix, skipMissing))
                return prefix;
        }

        return null;
    }

    private static bool IsReparsePath(string path, bool skipMissing)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (skipMissing && (ex is DirectoryNotFoundException or FileNotFoundException))
        {
            return false;
        }
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
