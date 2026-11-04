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

    /// <summary>.dsh 根必须同时存在的顶层结构目录（architecture.md §5 受控区域；ROOT-20261104：attachments 为 DSH 懒创建，降为可选）。</summary>
    private static readonly string[] DshRequiredDirs = ["sessions", "storages"];

    /// <summary>可选标记：存在时仍须通过 PathGuard 复验，缺失不影响根有效性（ROOT-20261104）。</summary>
    private static readonly string[] DshOptionalMarkerDirs = ["attachments"];

    /// <summary>桌面根必备目录与任一佐证目录（architecture.md §5 DesktopRoot 白名单）。</summary>
    private const string DesktopRequiredDir = "Cache";

    private static readonly string[] DesktopEvidenceDirs = ["Code Cache", "GPUCache", "logs"];

    private readonly Func<Environment.SpecialFolder, string> _folderPath;
    private readonly Func<string, string?> _environmentVariable;

    public RootLocator() : this(Environment.GetFolderPath, Environment.GetEnvironmentVariable) { }

    /// <summary>Inject account/environment lookups for isolated tests; production uses the parameterless constructor.</summary>
    public RootLocator(Func<Environment.SpecialFolder, string> folderPath, Func<string, string?> environmentVariable)
    {
        _folderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
        _environmentVariable = environmentVariable ?? throw new ArgumentNullException(nameof(environmentVariable));
    }

    /// <summary>
    /// Discover only explicit, saved, environment and current-account defaults, without choosing a root.
    /// Saved DSH input is the data home, not its sessions child. Callers must honor RememberRoots.
    /// Invalid environment input does not suppress the independent default candidate.
    /// </summary>
    public RootDiscoveryResult DiscoverCandidates(
        string? selectedDshHome = null, string? savedDshHome = null,
        string? selectedDesktopHome = null, string? savedDesktopHome = null)
    {
        var candidates = new List<RootCandidate>();
        Add(RootCandidateKind.DshHome, selectedDshHome, RootCandidateSource.UserSelected);
        Add(RootCandidateKind.DshHome, savedDshHome, RootCandidateSource.Saved);
        Add(RootCandidateKind.DshHome, _environmentVariable(DshHomeEnvironmentVariable), RootCandidateSource.EnvironmentVariable);
        Add(RootCandidateKind.DshHome, Path.Combine(_folderPath(Environment.SpecialFolder.UserProfile), ".dsh"), RootCandidateSource.Default);
        Add(RootCandidateKind.DesktopHome, selectedDesktopHome, RootCandidateSource.UserSelected);
        Add(RootCandidateKind.DesktopHome, savedDesktopHome, RootCandidateSource.Saved);
        Add(RootCandidateKind.DesktopHome, Path.Combine(_folderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop"), RootCandidateSource.Default);
        return new RootDiscoveryResult(candidates.AsReadOnly());

        void Add(RootCandidateKind kind, string? input, RootCandidateSource source)
        {
            if (string.IsNullOrWhiteSpace(input)) return;
            var inputError = InputError(input);
            if (inputError is not null)
            {
                candidates.Add(new RootCandidate(kind, input, null, Array.AsReadOnly(new[] { source }), RootCandidateStatus.Invalid, inputError));
                return;
            }
            string normalized;
            try { normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                candidates.Add(new RootCandidate(kind, input, null, Array.AsReadOnly(new[] { source }), RootCandidateStatus.Invalid, RootInvalidErrorCode));
                return;
            }

            var existing = candidates.FindIndex(x => x.Kind == kind && string.Equals(x.NormalizedPath, normalized,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (existing >= 0)
            {
                var candidate = candidates[existing];
                candidates[existing] = candidate with { Sources = Array.AsReadOnly(candidate.Sources.Append(source).Distinct().ToArray()) };
                return;
            }

            var (status, reason) = ProbeCandidate(normalized, kind);
            candidates.Add(new RootCandidate(kind, input, normalized, Array.AsReadOnly(new[] { source }), status, reason));
        }
    }

    private static (RootCandidateStatus Status, string? Reason) ProbeCandidate(string path, RootCandidateKind kind)
    {
        try
        {
            // 入口已单次校验：Add/Normalize 各链在 InputError 之后才规范化并到达此处。
            // Walk path components only, never recurse through data. Check ancestors before following them.
            // 根祖先 reparse 唯一防线：PathGuard.Check(path, path) 在 relative="." 时直接返回 PATH_ESCAPE，
            // ValidateRoot 只查根自身，均不覆盖根之上的祖先（S04 复核确认，勿删）。
            var prefix = Path.GetPathRoot(path)!;
            foreach (var segment in path[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                prefix = Path.Combine(prefix, segment);
                if ((File.GetAttributes(prefix) & FileAttributes.ReparsePoint) != 0)
                    return (RootCandidateStatus.Invalid, PathGuard.PathReparseErrorCode);
            }
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) == 0) return (RootCandidateStatus.Invalid, RootInvalidErrorCode);
            // Self-target PATH_ESCAPE indicates an ordinary root, not a deletion target.
            var check = new PathGuard().Check(path, path);
            if (check.ErrorCode != PathGuard.PathEscapeErrorCode)
                return (check.ErrorCode == PathGuard.FileAccessDeniedErrorCode ? RootCandidateStatus.Inaccessible : RootCandidateStatus.Invalid,
                    check.ErrorCode ?? RootInvalidErrorCode);
            var dirs = TopLevelDirectories(path);
            var markers = kind == RootCandidateKind.DshHome ? DshRequiredDirs.Concat(DshOptionalMarkerDirs).ToArray()
                : new[] { DesktopRequiredDir }.Concat(DesktopEvidenceDirs).ToArray();
            var valid = kind == RootCandidateKind.DshHome ? DshRequiredDirs.All(dirs.Contains)
                : dirs.Contains(DesktopRequiredDir) && DesktopEvidenceDirs.Any(dirs.Contains);
            foreach (var marker in markers)
            {
                if (!dirs.Contains(marker)) continue;
                var markerPath = Path.Combine(path, marker);
                var markerCheck = new PathGuard().Check(path, markerPath, allowDirectory: true);
                if (!markerCheck.IsAllowed)
                    return (markerCheck.ErrorCode == PathGuard.FileAccessDeniedErrorCode ? RootCandidateStatus.Inaccessible : RootCandidateStatus.Invalid,
                        markerCheck.ErrorCode ?? "ROOT_MARKERS_MISSING");
                // Ensure the current account can enumerate this controlled area, without recursive scanning.
                using var probe = Directory.EnumerateFileSystemEntries(markerPath).GetEnumerator();
                probe.MoveNext();
            }
            return valid ? (RootCandidateStatus.Valid, null) : (RootCandidateStatus.Invalid, "ROOT_MARKERS_MISSING");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (RootCandidateStatus.Missing, "ROOT_MISSING");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (RootCandidateStatus.Inaccessible, PathGuard.FileAccessDeniedErrorCode);
        }
    }

    public ResolvedRoots Resolve(string? selectedDshHome = null, string? selectedDesktopHome = null)
    {
        var (dshHome, dshSource, dshError) = SelectDshHome(selectedDshHome);
        var (desktopHome, desktopSource, desktopError) = SelectDesktopHome(selectedDesktopHome);

        var dshProbe = Probe(dshHome, dshSource, dshError, RootCandidateKind.DshHome);
        var desktopProbe = Probe(desktopHome, desktopSource, desktopError, RootCandidateKind.DesktopHome);

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

    private (string Home, RootSource Source, string? Error) SelectDshHome(string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            return Normalize(selected!, RootSource.UserSelected);

        var fromEnv = _environmentVariable(DshHomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Normalize(fromEnv!, RootSource.EnvironmentVariable);

        var userProfile = _folderPath(Environment.SpecialFolder.UserProfile);
        return Normalize(Path.Combine(userProfile, ".dsh"), RootSource.Default);
    }

    private (string Home, RootSource Source, string? Error) SelectDesktopHome(string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            return Normalize(selected!, RootSource.UserSelected);

        var appData = _folderPath(Environment.SpecialFolder.ApplicationData);
        return Normalize(Path.Combine(appData, "@deepseek-ai", "dsh-desktop"), RootSource.Default);
    }

    private static (string Home, RootSource Source, string? Error) Normalize(string path, RootSource source)
    {
        if (InputError(path) is not null) return (path, source, RootInvalidErrorCode);
        try
        {
            return (Path.GetFullPath(path), source, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (path, source, RootInvalidErrorCode);
        }
    }

    private static RootProbe Probe(string home, RootSource source, string? normalizeError, RootCandidateKind kind)
    {
        if (normalizeError is not null)
            return new RootProbe(home, source, RootProbeStatus.NotHarness, normalizeError);

        var (status, reason) = ProbeCandidate(home, kind);
        return status switch
        {
            RootCandidateStatus.Valid => new RootProbe(home, source, RootProbeStatus.Valid, null),
            RootCandidateStatus.Missing => new RootProbe(home, source, RootProbeStatus.Missing, null),
            _ => new RootProbe(home, source, RootProbeStatus.NotHarness,
                reason == "ROOT_MARKERS_MISSING" ? RootInvalidErrorCode : reason),
        };
    }

    private static string? InputError(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "ROOT_UNC_OR_DEVICE";
        if (!Path.IsPathFullyQualified(path)) return "ROOT_NOT_ABSOLUTE";
        if (path.IndexOf('\0') >= 0 || (path.Length > 2 && path.IndexOf(':', 2) >= 0)) return RootInvalidErrorCode;
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..") return "ROOT_TRAVERSAL";
            if (segment != "." && (segment.EndsWith(' ') || segment.EndsWith('.'))) return "ROOT_AMBIGUOUS_SEGMENT";
        }
        return null;
    }

    private static HashSet<string> TopLevelDirectories(string home)
    {
        // 只读枚举顶层目录（EnumerateDirectories 返回完整路径，需取目录名）；Windows 路径大小写不敏感。
        return new HashSet<string>(
            Directory.EnumerateDirectories(home).Select(dir => Path.GetFileName(dir)!),
            StringComparer.OrdinalIgnoreCase);
    }
}
