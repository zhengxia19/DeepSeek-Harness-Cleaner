using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-20 独立设置的保存与读取（TC-19、REQ-019、interfaces.md §4）。
/// 全部在夹具临时根下注入路径执行，绝不触碰真实 %LOCALAPPDATA% 或 Harness 数据根。
/// </summary>
public sealed class SettingsTests
{
    private static string SettingsPath(FixtureRoot fx) =>
        Path.Combine(fx.Root, "settings", SettingsStore.FileName);

    [Fact]
    public void MissingFile_LoadsDefaults_WithoutCreatingFile()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));

        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.Default, result.Status);
        Assert.Equal(SettingsStore.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.False(result.Settings.RememberRoots);
        Assert.Null(result.Settings.SessionRoot);
        Assert.Null(result.Settings.DesktopRoot);
        Assert.Null(result.Settings.BackupRoot);
        Assert.Equal(DateFilterField.FileModified, result.Settings.DateField);
        Assert.Equal(2000, result.Settings.PreviewMaxItems);
        Assert.Equal(1100, result.Settings.WindowWidth);
        Assert.Equal(700, result.Settings.WindowHeight);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRootsDateFieldAndWindow()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        var sessionRoot = Path.Combine(fx.Root, "chosen-sessions");
        var backupRoot = Path.Combine(fx.Root, "chosen-backups");

        var saved = store.Save(new AppSettings
        {
            RememberRoots = true,
            SessionRoot = sessionRoot,
            BackupRoot = backupRoot,
            DateField = DateFilterField.CreatedAt,
            PreviewMaxItems = 500,
            WindowWidth = 1280,
            WindowHeight = 800,
        });

        Assert.Equal(SettingsSaveStatus.Saved, saved.Status);
        var raw = File.ReadAllText(store.FilePath);
        Assert.Contains("\"schemaVersion\": 1", raw, StringComparison.Ordinal);

        var loaded = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.True(loaded.Settings.RememberRoots);
        Assert.Equal(sessionRoot, loaded.Settings.SessionRoot);
        Assert.Equal(backupRoot, loaded.Settings.BackupRoot);
        Assert.Null(loaded.Settings.DesktopRoot);
        Assert.Equal(DateFilterField.CreatedAt, loaded.Settings.DateField);
        Assert.Equal(500, loaded.Settings.PreviewMaxItems);
        Assert.Equal(1280, loaded.Settings.WindowWidth);
        Assert.Equal(800, loaded.Settings.WindowHeight);
    }

    [Fact]
    public void RememberRootsFalse_ConfigContainsNoAbsolutePaths()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        var absolute = Path.Combine(fx.Root, "chosen-sessions");

        // 先以 rememberRoots=true 写入历史绝对路径，再关闭记忆并保存。
        Assert.Equal(SettingsSaveStatus.Saved, store.Save(new AppSettings
        {
            RememberRoots = true,
            SessionRoot = absolute,
            DesktopRoot = Path.Combine(fx.Root, "chosen-desktop"),
            BackupRoot = Path.Combine(fx.Root, "chosen-backups"),
        }).Status);
        Assert.Equal(SettingsSaveStatus.Saved, store.Save(new AppSettings
        {
            RememberRoots = false,
            SessionRoot = absolute,
        }).Status);

        var raw = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(fx.Root, raw, StringComparison.Ordinal);
        Assert.Contains("\"sessionRoot\": null", raw, StringComparison.Ordinal);
        Assert.Contains("\"desktopRoot\": null", raw, StringComparison.Ordinal);
        Assert.Contains("\"backupRoot\": null", raw, StringComparison.Ordinal);

        var loaded = store.Load();
        Assert.Null(loaded.Settings.SessionRoot);
        Assert.Null(loaded.Settings.DesktopRoot);
        Assert.Null(loaded.Settings.BackupRoot);
    }

    [Fact]
    public void CorruptJson_FallsBackToDefaults_AndSaveRefusesToOverwrite()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ \"schemaVersion\": 1, oops");
        var before = File.ReadAllText(store.FilePath);

        var loaded = store.Load();
        Assert.Equal(SettingsLoadStatus.CorruptFallback, loaded.Status);
        Assert.Equal(AppSettings.Default, loaded.Settings);
        Assert.Equal(before, File.ReadAllText(store.FilePath));

        var saved = store.Save(new AppSettings { WindowWidth = 1500 });
        Assert.Equal(SettingsSaveStatus.RejectedCorrupt, saved.Status);
        Assert.Equal(before, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void UnknownSchemaVersion_FallsBackAndIsNeverOverwritten()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var future = """
            { "schemaVersion": 99, "rememberRoots": true, "sessionRoot": "Z:\\future-root" }
            """;
        File.WriteAllText(store.FilePath, future);

        var loaded = store.Load();
        Assert.Equal(SettingsLoadStatus.UnknownSchema, loaded.Status);
        Assert.Equal(AppSettings.Default, loaded.Settings);
        Assert.Contains("99", loaded.Detail!, StringComparison.Ordinal);

        var saved = store.Save(new AppSettings { RememberRoots = true, SessionRoot = "D:\\mine" });
        Assert.Equal(SettingsSaveStatus.RejectedUnknownSchema, saved.Status);
        Assert.Equal(future, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void OversizedPreviewMaxItems_IsClampedToDesignLimit()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, """
            { "schemaVersion": 1, "previewMaxItems": 999999999 }
            """);

        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(SettingsStore.PreviewMaxItemsLimit, loaded.Settings.PreviewMaxItems);
    }

    [Fact]
    public void UnknownDateFieldToken_FallsBackToFileModified()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, """
            { "schemaVersion": 1, "dateField": "lastLoginAt" }
            """);

        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(DateFilterField.FileModified, loaded.Settings.DateField);
    }

    [Fact]
    public void RelativeRoot_IsNotPersisted()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));

        Assert.Equal(SettingsSaveStatus.Saved, store.Save(new AppSettings
        {
            RememberRoots = true,
            SessionRoot = "relative/path",
        }).Status);

        var loaded = store.Load();
        Assert.True(loaded.Settings.RememberRoots);
        Assert.Null(loaded.Settings.SessionRoot);
    }

    [Fact]
    public void Save_UsesAtomicReplace_AndLeavesNoTempResidue()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));

        Assert.Equal(SettingsSaveStatus.Saved, store.Save(new AppSettings { WindowWidth = 1024 }).Status);
        Assert.Equal(SettingsSaveStatus.Saved, store.Save(new AppSettings { WindowWidth = 1025 }).Status);

        var directory = Path.GetDirectoryName(store.FilePath)!;
        var files = Directory.GetFiles(directory);
        Assert.Equal(new[] { SettingsStore.FileName }, files.Select(name => Path.GetFileName(name)!).ToArray());
        Assert.Contains("\"windowWidth\": 1025", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownExtraFields_AreIgnoredForForwardCompatibility()
    {
        using var fx = FixtureRoot.Create();
        var store = new SettingsStore(SettingsPath(fx));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, """
            { "schemaVersion": 1, "windowWidth": 1200, "futureToggles": { "a": true }, "newField": "x" }
            """);

        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(1200, loaded.Settings.WindowWidth);
    }
}
