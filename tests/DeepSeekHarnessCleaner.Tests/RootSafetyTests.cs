using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

[SupportedOSPlatform("windows")]
public sealed class RootSafetyTests
{
    private static RootLocator Locator(FixtureRoot root, string? environment = null) => new(
        folder => folder == Environment.SpecialFolder.UserProfile ? root.Root : root.DesktopHome,
        _ => environment);

    [Theory]
    [InlineData("relative", "ROOT_NOT_ABSOLUTE")]
    [InlineData("C:relative", "ROOT_NOT_ABSOLUTE")]
    [InlineData("\\\\server\\share\\data", "ROOT_UNC_OR_DEVICE")]
    [InlineData("\\\\?\\C:\\data", "ROOT_UNC_OR_DEVICE")]
    [InlineData("\\\\.\\C:\\data", "ROOT_UNC_OR_DEVICE")]
    [InlineData("C:\\data\\..\\other", "ROOT_TRAVERSAL")]
    [InlineData("C:\\data:stream", "ROOT_INVALID")]
    [InlineData("C:\\data.\\child", "ROOT_AMBIGUOUS_SEGMENT")]
    [InlineData("C:\\data ", "ROOT_AMBIGUOUS_SEGMENT")]
    public void UnsafeInput_IsRejectedBeforeSourceAccess(string input, string reason)
    {
        using var root = FixtureRoot.Create();
        var locator = Locator(root);
        var candidate = Assert.Single(locator.DiscoverCandidates(selectedDshHome: input).Candidates,
            x => x.Sources.Contains(RootCandidateSource.UserSelected));
        Assert.Equal(RootCandidateStatus.Invalid, candidate.Status);
        Assert.Equal(reason, candidate.Reason);
        Assert.Null(candidate.NormalizedPath);
        Assert.Equal(RootProbeStatus.NotHarness, locator.Resolve(selectedDshHome: input).DshProbe.Status);
    }

    [Fact]
    public void DriveProfileAndExecutableRoots_AreRejected()
    {
        using var root = FixtureRoot.Create();
        foreach (var input in new[] { Path.GetPathRoot(root.Root)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppContext.BaseDirectory })
        {
            var candidate = Assert.Single(Locator(root).DiscoverCandidates(selectedDshHome: input).Candidates,
                x => x.Sources.Contains(RootCandidateSource.UserSelected));
            Assert.Equal(RootCandidateStatus.Invalid, candidate.Status);
            Assert.Equal(PathGuard.RootInvalidErrorCode, candidate.Reason);
        }
    }

    [Fact]
    public void MissingMarkersAndMissingRoot_AreDistinct_AndNeitherIsCreated()
    {
        using var root = FixtureRoot.Create();
        var partial = Path.Combine(root.Root, "partial");
        Directory.CreateDirectory(Path.Combine(partial, "sessions"));
        var missing = Path.Combine(root.Root, "missing");
        var result = Locator(root).DiscoverCandidates(selectedDshHome: partial, savedDshHome: missing);
        var invalid = Assert.Single(result.Candidates, x => x.Sources.Contains(RootCandidateSource.UserSelected));
        Assert.Equal(RootCandidateStatus.Invalid, invalid.Status);
        Assert.Equal("ROOT_MARKERS_MISSING", invalid.Reason);
        var absent = Assert.Single(result.Candidates, x => x.Sources.Contains(RootCandidateSource.Saved));
        Assert.Equal(RootCandidateStatus.Missing, absent.Status);
        Assert.Equal("ROOT_MISSING", absent.Reason);
        Assert.False(Directory.Exists(Path.Combine(partial, "storages")));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReparseAncestorOrControlledMarker_IsRejectedByDiscoveryAndResolve(bool marker)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("synthetic sentinel"));
        var hash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var link = Path.Combine(root.Root, "alias");
        var home = marker ? Path.Combine(root.Root, "selected-home") : Path.Combine(link, ".dsh");
        if (marker)
        {
            Directory.CreateDirectory(Path.Combine(home, "storages"));
            Directory.CreateDirectory(Path.Combine(home, "attachments"));
            link = Path.Combine(home, "sessions");
        }
        try
        {
            Directory.CreateSymbolicLink(link, marker ? outside.SessionsRoot : outside.Root);
            var locator = Locator(root);
            var candidate = Assert.Single(locator.DiscoverCandidates(selectedDshHome: home).Candidates,
                x => x.Sources.Contains(RootCandidateSource.UserSelected));
            Assert.Equal(RootCandidateStatus.Invalid, candidate.Status);
            Assert.Equal(PathGuard.PathReparseErrorCode, candidate.Reason);
            Assert.Equal(PathGuard.PathReparseErrorCode, locator.Resolve(selectedDshHome: home).DshProbe.ErrorCode);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualAclDenial_IsInaccessible_NotMissing_AndRestored(bool denyMarker)
    {
        using var root = FixtureRoot.Create();
        using var denied = FixtureRoot.Create();
        var path = denyMarker ? denied.SessionsRoot : denied.DshHome;
        var directory = new DirectoryInfo(path);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var restricted = directory.GetAccessControl(AccessControlSections.Access);
        var sid = WindowsIdentity.GetCurrent().User!;
        restricted.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(restricted);
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(path));
            var locator = Locator(root);
            var candidate = Assert.Single(locator.DiscoverCandidates(selectedDshHome: denied.DshHome).Candidates,
                x => x.Sources.Contains(RootCandidateSource.UserSelected));
            Assert.Equal(RootCandidateStatus.Inaccessible, candidate.Status);
            Assert.Equal(PathGuard.FileAccessDeniedErrorCode, candidate.Reason);
            var legacy = locator.Resolve(selectedDshHome: denied.DshHome).DshProbe;
            Assert.Equal(RootProbeStatus.NotHarness, legacy.Status);
            Assert.Equal(PathGuard.FileAccessDeniedErrorCode, legacy.ErrorCode);
        }
        finally
        {
            // A freshly fetched unchanged ACL has no dirty sections; mark Access explicitly for restoration.
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
        Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.NotNull(Directory.GetFileSystemEntries(path));
    }

    [Fact]
    public void SourcesAreBounded_DuplicatesRetainOrigins_NoRecursiveDiscoveryOrMutation()
    {
        using var root = FixtureRoot.Create();
        using var unrelated = FixtureRoot.Create();
        var hiddenHome = Path.Combine(root.Root, "nested", ".dsh");
        foreach (var marker in new[] { "sessions", "storages", "attachments" }) Directory.CreateDirectory(Path.Combine(hiddenHome, marker));
        var before = Directory.GetFileSystemEntries(root.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var result = Locator(root, root.DshHome).DiscoverCandidates(root.DshHome, root.DshHome);
        var dsh = Assert.Single(result.Candidates, x => x.Kind == RootCandidateKind.DshHome);
        Assert.Equal(4, dsh.Sources.Count);
        Assert.Equal(RootCandidateStatus.Valid, dsh.Status);
        Assert.DoesNotContain(result.Candidates, x => x.NormalizedPath == hiddenHome || x.NormalizedPath == unrelated.DshHome);
        Assert.Equal(before, Directory.GetFileSystemEntries(root.Root, "*", SearchOption.AllDirectories).OrderBy(x => x));
    }

    [Fact]
    public void EmptyAccountLookup_DoesNotTurnWorkingDirectoryIntoDefaultRoot()
    {
        var result = new RootLocator(_ => "", _ => null).DiscoverCandidates();
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, x =>
        {
            Assert.Equal(RootCandidateStatus.Invalid, x.Status);
            Assert.Equal("ROOT_NOT_ABSOLUTE", x.Reason);
        });
    }
}
