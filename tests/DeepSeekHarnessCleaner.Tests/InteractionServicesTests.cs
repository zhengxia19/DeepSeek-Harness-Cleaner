using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class InteractionServicesTests
{
    [Theory]
    [InlineData("png", ".png")]
    [InlineData("jpeg", ".jpg")]
    [InlineData("gif", ".gif")]
    [InlineData("webp", ".webp")]
    [InlineData("pdf", ".pdf")]
    public void ExtensionlessAttachment_UsesVerifiedCopy_OriginalAndSentinelUnchanged(string type, string extension)
    {
        using var fx = FixtureRoot.Create();
        var bytes = type switch
        {
            "png" => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 },
            "jpeg" => new byte[] { 255, 216, 255, 0 },
            "gif" => "GIF89a synthetic"u8.ToArray(),
            "webp" => "RIFF1234WEBPdata"u8.ToArray(),
            _ => "%PDF-1.7 synthetic"u8.ToArray(),
        };
        fx.Factory.WriteFile(fx.AttachmentsRoot, "object", bytes);
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        var sentinel = fx.Factory.WriteFile(fx.Root, "sentinel.txt", [1, 2, 3]);
        string? launched = null;
        var opener = new AttachmentOpener(path => launched = path, Path.Combine(fx.Root, "opened"));
        var result = opener.Open(fx.DshHome, item);
        Assert.True(result.Success, result.Error);
        Assert.Equal(extension, Path.GetExtension(launched));
        Assert.NotEqual(item.Candidate.FullPath, launched);
        Assert.Equal(bytes, File.ReadAllBytes(launched!));
        Assert.Equal(bytes, File.ReadAllBytes(item.Candidate.FullPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(sentinel));
        Assert.True(opener.Open(fx.DshHome, item).Success);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(fx.Root, "opened")).Length);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("same-length")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("canceled")]
    [InlineData("launch-failed")]
    [InlineData("outside")]
    public void AttachmentOpening_FailuresNeverLaunchOrMutateOtherFiles(string scenario)
    {
        using var fx = FixtureRoot.Create();
        var file = fx.Factory.WriteFile(fx.AttachmentsRoot, scenario == "unknown" ? "object" : "中文 空格 & 文件.txt", Encoding.UTF8.GetBytes("synthetic"));
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        var time = File.GetLastWriteTimeUtc(file);
        if (scenario == "changed") File.AppendAllText(file, "changed");
        if (scenario == "same-length") { File.WriteAllText(file, "Synthetic"); File.SetLastWriteTimeUtc(file, time); }
        if (scenario == "missing") File.Delete(file);
        if (scenario == "outside")
        {
            var outside = fx.Factory.WriteFile(fx.Root, "outside.txt", [1, 2, 3]);
            item = item with { Candidate = item.Candidate with { FullPath = outside } };
        }
        var launched = false;
        var opener = new AttachmentOpener(_ =>
        {
            if (scenario == "launch-failed") throw new System.ComponentModel.Win32Exception(1155);
            launched = true;
        }, Path.Combine(fx.Root, "opened"));
        var result = opener.Open(fx.DshHome, item, new CancellationToken(scenario == "canceled"));
        Assert.False(result.Success);
        Assert.False(launched);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void NamedAttachment_LaunchesExactPathWithoutShellArguments()
    {
        using var fx = FixtureRoot.Create();
        var file = fx.Factory.WriteFile(fx.AttachmentsRoot, "中文 空格 & test.txt", "fixture"u8.ToArray());
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        string? path = null;
        Assert.True(new AttachmentOpener(value => path = value, Path.Combine(fx.Root, "opened")).Open(fx.DshHome, item).Success);
        Assert.Equal(file, path);
        Assert.False(Directory.Exists(Path.Combine(fx.Root, "opened")));
    }

    [Fact]
    public void FailedCopyLaunch_RemovesOnlyNewCopy()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.AttachmentsRoot, "object", "%PDF-1.7 synthetic"u8.ToArray());
        var root = Path.Combine(fx.Root, "opened");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keep.pdf"), "keep");
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        var result = new AttachmentOpener(_ => throw new System.ComponentModel.Win32Exception(1155), root).Open(fx.DshHome, item);
        Assert.False(result.Success);
        Assert.Equal("keep.pdf", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
    }

    [Theory]
    [InlineData("{", false)]
    [InlineData("{\"schemaVersion\":99}", false)]
    [InlineData("{\"schemaVersion\":1,\"normalBounds\":null,\"windowState\":\"Normal\",\"savedDpi\":96}", false)]
    public void WindowState_CorruptOrUnknownFileIsNeverOverwritten(string json, bool expected)
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteFile(fx.Root, "window.json", Encoding.UTF8.GetBytes(json));
        var store = new WindowStateStore(path);
        Assert.Equal(expected, store.Load().CanSave);
        Assert.NotNull(store.Save(State()));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void WindowState_Roundtrip_OnlyWindowFields_NoTemporaryRemains()
    {
        using var fx = FixtureRoot.Create();
        var path = Path.Combine(fx.Root, "settings", "window.json");
        var store = new WindowStateStore(path);
        Assert.Null(store.Load().State);
        Assert.Null(store.Save(State()));
        Assert.Equal(State(), store.Load().State);
        Assert.Null(store.Save(State() with { WindowState = "Maximized" }));
        Assert.Equal("Maximized", store.Load().State!.WindowState);
        Assert.Equal(new[] { "window.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        Assert.DoesNotContain("session", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowState_LockedDestinationKeepsOldRecordAndCleansTemporary()
    {
        using var fx = FixtureRoot.Create();
        var path = Path.Combine(fx.Root, "window.json");
        var store = new WindowStateStore(path);
        Assert.Null(store.Save(State()));
        var before = File.ReadAllBytes(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(store.Save(State() with { SavedDpi = 144 }));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(fx.Root, "*.tmp-*"));
    }

    [Theory]
    [InlineData(-1700, 100, 96, -1700, 100, 1000, 700)]
    [InlineData(9000, 9000, 96, 920, 380, 1000, 700)]
    [InlineData(100, 100, 144, 100, 30, 1500, 1050)]
    public void WindowState_NormalizationSupportsNegativeMonitorCoordinatesAndDpi(int x, int y, int dpi, int expectedX, int expectedY, int width, int height)
    {
        var state = State() with { NormalBounds = new WindowRectangle(x, y, 1000, 700) };
        var fitted = WindowStateStore.Fit(state, [new Rectangle(0, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1080)], new Size(640, 420), dpi);
        Assert.Equal(new Rectangle(expectedX, expectedY, width, height), fitted);
    }

    private static WindowStateRecord State() => new(1, new WindowRectangle(100, 100, 1000, 700), "Normal", 96);

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void UnwritableToolDirectory_FailsWithoutOverwritingOrRedirecting()
    {
        using var fx = FixtureRoot.Create();
        var directory = new DirectoryInfo(Path.Combine(fx.Root, "readonly-tool"));
        directory.Create();
        var store = new WindowStateStore(Path.Combine(directory.FullName, "window.json"));
        Assert.Null(store.Save(State()));
        var before = File.ReadAllBytes(store.FilePath);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        var originalSddl = original.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        var restricted = new System.Security.AccessControl.DirectorySecurity();
        restricted.SetSecurityDescriptorSddlForm(originalSddl, System.Security.AccessControl.AccessControlSections.Access);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        restricted.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.Write,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restricted);
            Assert.NotNull(store.Save(State() with { SavedDpi = 144 }));
            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp-*"));
        }
        finally
        {
            var restored = new System.Security.AccessControl.DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(originalSddl, System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restored);
        }
    }

    [Theory]
    [InlineData("source-link")]
    [InlineData("copy-link")]
    [InlineData("window-link")]
    public void InteractionStores_RejectReparseAndPreserveOutside(string scenario)
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.AttachmentsRoot, "object", "%PDF-1.7 synthetic"u8.ToArray());
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        var outside = Path.Combine(fx.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.bin");
        File.WriteAllBytes(sentinel, [1, 2, 3]);
        var before = SHA256.HashData(File.ReadAllBytes(sentinel));
        var link = Path.Combine(fx.Root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            if (scenario == "window-link")
            {
                var store = new WindowStateStore(Path.Combine(link, "state.json"));
                Assert.False(store.Load().CanSave);
                Assert.NotNull(store.Save(State()));
            }
            else
            {
                if (scenario == "source-link") item = item with { Candidate = item.Candidate with { FullPath = Path.Combine(link, "sentinel.bin") } };
                var launched = false;
                var opener = new AttachmentOpener(_ => launched = true, scenario == "copy-link" ? link : Path.Combine(fx.Root, "opened"));
                Assert.False(opener.Open(fx.DshHome, item).Success);
                Assert.False(launched);
            }
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(sentinel)));
            Assert.Equal("sentinel.bin", Path.GetFileName(Assert.Single(Directory.GetFiles(outside))));
        }
        finally { Directory.Delete(link); }
    }
}
