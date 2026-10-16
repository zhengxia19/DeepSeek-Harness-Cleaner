using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// 授权数据根定位器（architecture.md §5、interfaces.md §2）。
/// 优先级：用户显式选择 &gt; DSH_HOME 环境变量 &gt; %USERPROFILE%\.dsh。
/// 桌面根：%APPDATA%\@deepseek-ai\dsh-desktop（可被用户选择覆盖）。
/// 只读探测：任何情况下不得创建、写入或删除源目录。
/// </summary>
public sealed class RootLocator
{
    public const string DshHomeEnvironmentVariable = "DSH_HOME";
    public const string RootInvalidErrorCode = "ROOT_INVALID";

    /// <summary>.dsh 根必须同时存在的顶层结构目录（architecture.md §5 三个受控区域）。</summary>
    private static readonly string[] DshRequiredDirs = ["sessions", "storages", "attachments"];

    /// <summary>桌面根必备目录与任一佐证目录（architecture.md §5 DesktopRoot 白名单）。</summary>
    private const string DesktopRequiredDir = "Cache";

    private static readonly string[] DesktopEvidenceDirs = ["Code Cache", "GPUCache", "logs"];

    public ResolvedRoots Resolve(string? selectedDshHome = null, string? selectedDesktopHome = null)
    {
        var (dshHome, dshSource, dshError) = SelectDshHome(selectedDshHome);
        var (desktopHome, desktopSource, desktopError) = SelectDesktopHome(selectedDesktopHome);

        var dshProbe = Probe(dshHome, dshSource, dshError, IsDshHome);
        var desktopProbe = Probe(desktopHome, desktopSource, desktopError, IsDesktopHome);

        // 派生子根仅是路径字符串；是否可进入扫描由 DshProbe/DesktopProbe 状态决定。
        return new ResolvedRoots(
            dshHome,
            Path.Combine(dshHome, "sessions"),
            Path.Combine(dshHome, "storages"),
            Path.Combine(dshHome, "attachments"),
            desktopHome,
            dshProbe,
            desktopProbe);
    }

    private static (string Home, RootSource Source, string? Error) SelectDshHome(string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            return Normalize(selected!, RootSource.UserSelected);

        var fromEnv = Environment.GetEnvironmentVariable(DshHomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Normalize(fromEnv!, RootSource.EnvironmentVariable);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Normalize(Path.Combine(userProfile, ".dsh"), RootSource.Default);
    }

    private static (string Home, RootSource Source, string? Error) SelectDesktopHome(string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            return Normalize(selected!, RootSource.UserSelected);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Normalize(Path.Combine(appData, "@deepseek-ai", "dsh-desktop"), RootSource.Default);
    }

    private static (string Home, RootSource Source, string? Error) Normalize(string path, RootSource source)
    {
        try
        {
            return (Path.GetFullPath(path), source, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (path, source, RootInvalidErrorCode);
        }
    }

    private static RootProbe Probe(string home, RootSource source, string? normalizeError, Func<string, bool> isHarness)
    {
        if (normalizeError is not null)
            return new RootProbe(home, source, RootProbeStatus.NotHarness, normalizeError);

        if (!Directory.Exists(home))
            return new RootProbe(home, source, RootProbeStatus.Missing, null);

        return isHarness(home)
            ? new RootProbe(home, source, RootProbeStatus.Valid, null)
            : new RootProbe(home, source, RootProbeStatus.NotHarness, RootInvalidErrorCode);
    }

    private static bool IsDshHome(string home)
    {
        var dirs = TopLevelDirectories(home);
        return DshRequiredDirs.All(d => dirs.Contains(d));
    }

    private static bool IsDesktopHome(string home)
    {
        var dirs = TopLevelDirectories(home);
        return dirs.Contains(DesktopRequiredDir) && DesktopEvidenceDirs.Any(dirs.Contains);
    }

    private static HashSet<string> TopLevelDirectories(string home)
    {
        // 只读枚举顶层目录（EnumerateDirectories 返回完整路径，需取目录名）；Windows 路径大小写不敏感。
        return new HashSet<string>(
            Directory.EnumerateDirectories(home).Select(dir => Path.GetFileName(dir)!),
            StringComparer.OrdinalIgnoreCase);
    }
}
