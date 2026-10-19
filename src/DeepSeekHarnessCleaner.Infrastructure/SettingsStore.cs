using System.Text.Json;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// 工具设置模型（interfaces.md §4，schemaVersion=1）。仅保存记忆路径开关、
/// 受控根路径、日期筛选偏好、预览上限与窗口几何（尺寸 + N-03 位置/最大化）；不含会话正文或凭据。
/// </summary>
public sealed record AppSettings
{
    public const int DefaultWindowWidth = 1100;
    public const int DefaultWindowHeight = 700;

    public int SchemaVersion { get; init; } = SettingsStore.CurrentSchemaVersion;
    public bool RememberRoots { get; init; }
    public string? SessionRoot { get; init; }
    public string? DesktopRoot { get; init; }
    public string? BackupRoot { get; init; }
    public DateFilterField DateField { get; init; } = DateFilterField.FileModified;
    public int PreviewMaxItems { get; init; } = SettingsStore.PreviewMaxItemsLimit;
    public int WindowWidth { get; init; } = DefaultWindowWidth;
    public int WindowHeight { get; init; } = DefaultWindowHeight;

    /// <summary>N-03：窗口位置（外层 Bounds 坐标，负值对多屏合法）；null = 未保存过。</summary>
    public int? WindowX { get; init; }

    public int? WindowY { get; init; }

    /// <summary>N-03：退出时是否处于最大化；null = 未保存过。</summary>
    public bool? WindowMaximized { get; init; }

    public static AppSettings Default { get; } = new AppSettings();
}

/// <summary>读取结果状态：文件不存在=Default；损坏/未知 schema 只读回退默认，不覆写原文件。</summary>
public enum SettingsLoadStatus
{
    Default,
    Loaded,
    CorruptFallback,
    UnknownSchema,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? Detail = null);

/// <summary>保存结果状态：损坏或未知 schema 的既有文件一律拒绝覆写（design.md §9）。</summary>
public enum SettingsSaveStatus
{
    Saved,
    RejectedCorrupt,
    RejectedUnknownSchema,
    WriteFailed,
}

public sealed record SettingsSaveResult(SettingsSaveStatus Status, string? Detail = null);

/// <summary>
/// T-20 独立设置存储（interfaces.md §4、design.md §9、REQ-019/TC-19）。
/// 默认位置 %LOCALAPPDATA%\DeepSeekHarnessCleaner\settings.json（工具私有目录，非 Harness 数据根）；
/// 测试注入夹具路径。读取对损坏/未知 schema 只读回退默认；保存经临时文件+替换原子写入，
/// 且既有文件损坏或 schemaVersion 未知时拒绝覆写。rememberRoots=false 时绝不持久化绝对路径。
/// </summary>
public sealed class SettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int PreviewMaxItemsLimit = 2000;
    public const string DirectoryName = "DeepSeekHarnessCleaner";
    public const string FileName = "settings.json";

    private const int MinWindowSide = 320;
    private const int MaxWindowSide = 8192;

    /// <summary>N-03：窗口坐标钳制界（负值对多屏布局合法；超出视为异常值）。</summary>
    public const int MaxWindowCoordinate = 1_000_000;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    /// <summary>使用默认工具私有配置位置；调用方（UI）负责真实环境。</summary>
    public SettingsStore() : this(DefaultFilePath())
    {
    }

    /// <summary>注入显式配置文件路径（测试须指向夹具/临时目录，不得写真实用户配置）。</summary>
    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public static string DefaultFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DirectoryName,
            FileName);

    /// <summary>读取设置；任何损坏或未知 schema 均回退默认值，且绝不改动磁盘文件。</summary>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(_filePath))
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.Default, null);

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.CorruptFallback, "配置文件无法读取：" + ex.Message);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.CorruptFallback, "配置文件不是有效 JSON：" + ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.CorruptFallback, "配置文件根节点不是对象。");

            var root = doc.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var versionElement)
                || !versionElement.TryGetInt32(out var version))
            {
                return new SettingsLoadResult(AppSettings.Default, SettingsLoadStatus.CorruptFallback, "缺少有效 schemaVersion。");
            }

            if (version != CurrentSchemaVersion)
                return new SettingsLoadResult(
                    AppSettings.Default,
                    SettingsLoadStatus.UnknownSchema,
                    $"未知 schemaVersion={version}：回退默认且不覆写原文件。");

            var rememberRoots = ReadBool(root, "rememberRoots");
            var settings = new AppSettings
            {
                SchemaVersion = CurrentSchemaVersion,
                RememberRoots = rememberRoots,
                SessionRoot = rememberRoots ? ReadRoot(root, "sessionRoot") : null,
                DesktopRoot = rememberRoots ? ReadRoot(root, "desktopRoot") : null,
                BackupRoot = rememberRoots ? ReadRoot(root, "backupRoot") : null,
                DateField = ReadDateField(root, "dateField"),
                PreviewMaxItems = ReadInt(root, "previewMaxItems", PreviewMaxItemsLimit, 1, PreviewMaxItemsLimit),
                WindowWidth = ReadInt(root, "windowWidth", AppSettings.DefaultWindowWidth, MinWindowSide, MaxWindowSide),
                WindowHeight = ReadInt(root, "windowHeight", AppSettings.DefaultWindowHeight, MinWindowSide, MaxWindowSide),
                WindowX = ReadNullableInt(root, "windowX", -MaxWindowCoordinate, MaxWindowCoordinate),
                WindowY = ReadNullableInt(root, "windowY", -MaxWindowCoordinate, MaxWindowCoordinate),
                WindowMaximized = ReadNullableBool(root, "windowMaximized"),
            };

            return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded, null);
        }
    }

    /// <summary>
    /// 保存设置：既有文件损坏或 schemaVersion 未知时拒绝写入（不覆写用户已有配置）；
    /// 正常写入经同目录临时文件 + 替换完成。rememberRoots=false 时根路径一律落盘为 null。
    /// </summary>
    public SettingsSaveResult Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (File.Exists(_filePath))
        {
            var gate = Load();
            switch (gate.Status)
            {
                case SettingsLoadStatus.CorruptFallback:
                    return new SettingsSaveResult(SettingsSaveStatus.RejectedCorrupt, gate.Detail);
                case SettingsLoadStatus.UnknownSchema:
                    return new SettingsSaveResult(SettingsSaveStatus.RejectedUnknownSchema, gate.Detail);
            }
        }

        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("设置文件必须位于某个目录内。");
        Directory.CreateDirectory(directory);

        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(ToDto(normalized), WriteOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            return new SettingsSaveResult(SettingsSaveStatus.WriteFailed, "写入设置失败：" + ex.Message);
        }

        return new SettingsSaveResult(SettingsSaveStatus.Saved, null);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        string? session = null;
        string? desktop = null;
        string? backup = null;
        if (settings.RememberRoots)
        {
            session = RootOrNull(settings.SessionRoot);
            desktop = RootOrNull(settings.DesktopRoot);
            backup = RootOrNull(settings.BackupRoot);
        }

        return settings with
        {
            SchemaVersion = CurrentSchemaVersion,
            SessionRoot = session,
            DesktopRoot = desktop,
            BackupRoot = backup,
            PreviewMaxItems = Math.Clamp(settings.PreviewMaxItems, 1, PreviewMaxItemsLimit),
            WindowWidth = settings.WindowWidth > 0
                ? Math.Clamp(settings.WindowWidth, MinWindowSide, MaxWindowSide)
                : AppSettings.DefaultWindowWidth,
            WindowHeight = settings.WindowHeight > 0
                ? Math.Clamp(settings.WindowHeight, MinWindowSide, MaxWindowSide)
                : AppSettings.DefaultWindowHeight,
            WindowX = ClampCoordinateOrNull(settings.WindowX),
            WindowY = ClampCoordinateOrNull(settings.WindowY),
            WindowMaximized = settings.WindowMaximized,
        };
    }

    private static int? ClampCoordinateOrNull(int? value) =>
        value is null ? null : Math.Clamp(value.Value, -MaxWindowCoordinate, MaxWindowCoordinate);

    private static SettingsDto ToDto(AppSettings settings) => new(
        settings.SchemaVersion,
        settings.RememberRoots,
        settings.SessionRoot,
        settings.DesktopRoot,
        settings.BackupRoot,
        DateFieldToToken(settings.DateField),
        settings.PreviewMaxItems,
        settings.WindowWidth,
        settings.WindowHeight,
        settings.WindowX,
        settings.WindowY,
        settings.WindowMaximized);

    private static string? RootOrNull(string? root)
    {
        if (root is null)
            return null;
        var trimmed = root.Trim();
        if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed))
            return null;
        return trimmed;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false,
        };

    private static string? ReadRoot(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return RootOrNull(value.GetString());
    }

    private static int ReadInt(JsonElement root, string name, int fallback, int min, int max)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    /// <summary>N-03：可选整数（窗口坐标）；缺失、null、非整数或非 int64 数值 → null（未保存），在界内则钳制。</summary>
    private static int? ReadNullableInt(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetInt64(out var parsed))
            return null;
        return (int)Math.Clamp(parsed, min, max);
    }

    private static bool? ReadNullableBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static DateFilterField ReadDateField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return DateFilterField.FileModified;
        return value.GetString() switch
        {
            var token when string.Equals(token, "createdAt", StringComparison.OrdinalIgnoreCase) => DateFilterField.CreatedAt,
            var token when string.Equals(token, "lastActivityAt", StringComparison.OrdinalIgnoreCase) => DateFilterField.LastActivityAt,
            _ => DateFilterField.FileModified,
        };
    }

    private static string DateFieldToToken(DateFilterField field) => field switch
    {
        DateFilterField.CreatedAt => "createdAt",
        DateFilterField.LastActivityAt => "lastActivityAt",
        DateFilterField.FileModified => "fileModified",
        _ => "fileModified",
    };

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
            // 临时文件残留不影响用户配置，忽略。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>落盘 DTO，属性顺序与 interfaces.md §4 契约一致。</summary>
    private sealed record SettingsDto(
        int SchemaVersion,
        bool RememberRoots,
        string? SessionRoot,
        string? DesktopRoot,
        string? BackupRoot,
        string DateField,
        int PreviewMaxItems,
        int WindowWidth,
        int WindowHeight,
        int? WindowX,
        int? WindowY,
        bool? WindowMaximized);
}
