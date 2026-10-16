using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using System.Runtime.InteropServices;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>TC-01：根路径发现——默认/自定义/不存在/非 Harness 目录按合同响应，且不创建源目录。</summary>
public sealed class RootLocatorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _originalDshHome = Environment.GetEnvironmentVariable(RootLocator.DshHomeEnvironmentVariable);

    public RootLocatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dshcleaner-rootlocator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable(RootLocator.DshHomeEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootLocator.DshHomeEnvironmentVariable, _originalDshHome);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private string MakeDir(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakeValidDshHome(string home)
    {
        foreach (var dir in new[] { "sessions", "storages", "attachments" })
            Directory.CreateDirectory(Path.Combine(home, dir));
    }

    [Fact]
    public void Default_UsesUserProfileDshAndAppDataDesktop()
    {
        var roots = new RootLocator().Resolve();

        var expectedDsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        var expectedDesktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop");

        Assert.Equal(Path.GetFullPath(expectedDsh), roots.DshHome);
        Assert.Equal(Path.Combine(expectedDsh, "sessions"), roots.SessionRoot);
        Assert.Equal(Path.Combine(expectedDsh, "storages"), roots.StorageRoot);
        Assert.Equal(Path.Combine(expectedDsh, "attachments"), roots.AttachmentRoot);
        Assert.Equal(Path.GetFullPath(expectedDesktop), roots.DesktopHome);
        Assert.Equal(RootSource.Default, roots.DshProbe.Source);
        Assert.Equal(RootSource.Default, roots.DesktopProbe.Source);

        // 本机真实根存在时必须被识别为有效 Harness 根（只读探测）。
        if (Directory.Exists(expectedDsh))
            Assert.Equal(RootProbeStatus.Valid, roots.DshProbe.Status);
        if (Directory.Exists(expectedDesktop))
            Assert.Equal(RootProbeStatus.Valid, roots.DesktopProbe.Status);
    }

    [Fact]
    public void CustomSelection_ValidSyntheticRoots_AreAccepted()
    {
        var dsh = MakeDir("custom-dsh");
        MakeValidDshHome(dsh);
        var desktop = MakeDir("custom-desktop");
        Directory.CreateDirectory(Path.Combine(desktop, "Cache"));
        Directory.CreateDirectory(Path.Combine(desktop, "logs"));

        var roots = new RootLocator().Resolve(dsh, desktop);

        Assert.Equal(RootProbeStatus.Valid, roots.DshProbe.Status);
        Assert.Equal(RootProbeStatus.Valid, roots.DesktopProbe.Status);
        Assert.Equal(RootSource.UserSelected, roots.DshProbe.Source);
        Assert.Equal(RootSource.UserSelected, roots.DesktopProbe.Source);
        Assert.Equal(Path.Combine(dsh, "sessions"), roots.SessionRoot);
        Assert.Equal(Path.Combine(dsh, "storages"), roots.StorageRoot);
        Assert.Equal(Path.Combine(dsh, "attachments"), roots.AttachmentRoot);
        Assert.Null(roots.DshProbe.ErrorCode);
    }

    [Fact]
    public void DshHomeEnvironmentVariable_UsedWhenNoSelection()
    {
        var dsh = MakeDir("env-dsh");
        MakeValidDshHome(dsh);
        Environment.SetEnvironmentVariable(RootLocator.DshHomeEnvironmentVariable, dsh);

        var roots = new RootLocator().Resolve();

        Assert.Equal(dsh, roots.DshHome);
        Assert.Equal(RootSource.EnvironmentVariable, roots.DshProbe.Source);
        Assert.Equal(RootProbeStatus.Valid, roots.DshProbe.Status);
    }

    [Fact]
    public void UserSelection_OverridesEnvironmentVariable()
    {
        var envDsh = MakeDir("env-ignored");
        MakeValidDshHome(envDsh);
        var selected = MakeDir("selected");
        MakeValidDshHome(selected);
        Environment.SetEnvironmentVariable(RootLocator.DshHomeEnvironmentVariable, envDsh);

        var roots = new RootLocator().Resolve(selectedDshHome: selected);

        Assert.Equal(selected, roots.DshHome);
        Assert.Equal(RootSource.UserSelected, roots.DshProbe.Source);
    }

    [Fact]
    public void MissingRoot_ReportsMissing_AndCreatesNothing()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist", ".dsh");
        var before = Directory.GetFileSystemEntries(_tempRoot);

        var roots = new RootLocator().Resolve(missing, Path.Combine(_tempRoot, "no-desktop"));

        Assert.Equal(RootProbeStatus.Missing, roots.DshProbe.Status);
        Assert.Equal(RootProbeStatus.Missing, roots.DesktopProbe.Status);
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "no-desktop")));
        Assert.Equal(before, Directory.GetFileSystemEntries(_tempRoot));
    }

    [Fact]
    public void EmptyDirectory_IsNotHarness_RootInvalid()
    {
        var empty = MakeDir("empty");
        var roots = new RootLocator().Resolve(empty, empty);

        Assert.Equal(RootProbeStatus.NotHarness, roots.DshProbe.Status);
        Assert.Equal(RootLocator.RootInvalidErrorCode, roots.DshProbe.ErrorCode);
        Assert.Equal(RootProbeStatus.NotHarness, roots.DesktopProbe.Status);
        Assert.Equal(RootLocator.RootInvalidErrorCode, roots.DesktopProbe.ErrorCode);
    }

    [Fact]
    public void PartialMarkers_AreNotHarness()
    {
        var partial = MakeDir("partial-dsh");
        Directory.CreateDirectory(Path.Combine(partial, "sessions"));
        Directory.CreateDirectory(Path.Combine(partial, "storages"));
        // 缺 attachments

        var roots = new RootLocator().Resolve(partial, null);

        Assert.Equal(RootProbeStatus.NotHarness, roots.DshProbe.Status);
        Assert.Equal(RootLocator.RootInvalidErrorCode, roots.DshProbe.ErrorCode);
    }

    [Fact]
    public void DesktopRoot_RequiresCachePlusEvidenceDir()
    {
        var cacheOnly = MakeDir("desktop-cache-only");
        Directory.CreateDirectory(Path.Combine(cacheOnly, "Cache"));
        var roots1 = new RootLocator().Resolve(null, cacheOnly);
        Assert.Equal(RootProbeStatus.NotHarness, roots1.DesktopProbe.Status);

        var cacheGpu = MakeDir("desktop-cache-gpu");
        Directory.CreateDirectory(Path.Combine(cacheGpu, "Cache"));
        Directory.CreateDirectory(Path.Combine(cacheGpu, "GPUCache"));
        var roots2 = new RootLocator().Resolve(null, cacheGpu);
        Assert.Equal(RootProbeStatus.Valid, roots2.DesktopProbe.Status);
    }

    [Fact]
    public void InvalidPathCharacters_AreRejectedWithoutThrowing()
    {
        var bad = "C:\\bad\0path"; // 内嵌 NUL：Path.GetFullPath 抛 ArgumentException
        var roots = new RootLocator().Resolve(bad, bad);

        Assert.Equal(RootProbeStatus.NotHarness, roots.DshProbe.Status);
        Assert.Equal(RootLocator.RootInvalidErrorCode, roots.DshProbe.ErrorCode);
        Assert.Equal(RootProbeStatus.NotHarness, roots.DesktopProbe.Status);
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnMarkerNames()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var dsh = MakeDir("case-dsh");
        Directory.CreateDirectory(Path.Combine(dsh, "Sessions"));
        Directory.CreateDirectory(Path.Combine(dsh, "Storages"));
        Directory.CreateDirectory(Path.Combine(dsh, "Attachments"));

        var roots = new RootLocator().Resolve(dsh, null);
        Assert.Equal(RootProbeStatus.Valid, roots.DshProbe.Status);
    }
}
