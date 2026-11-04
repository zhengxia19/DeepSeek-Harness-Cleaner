using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class RootDiscoveryTests
{
    private static RootLocator Locator(FixtureRoot account, string? environmentHome = null) => new(
        folder => folder == Environment.SpecialFolder.UserProfile ? account.Root : account.DesktopHome,
        _ => environmentHome);

    private static string MakeDefault(FixtureRoot account)
    {
        // FixtureRoot already owns Root/.dsh with the required markers.
        Directory.CreateDirectory(Path.Combine(account.DesktopHome, "@deepseek-ai", "dsh-desktop", "Cache"));
        Directory.CreateDirectory(Path.Combine(account.DesktopHome, "@deepseek-ai", "dsh-desktop", "logs"));
        return account.DshHome;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("not-harness")]
    public void BadEnvironment_DoesNotHideValidDefault(string scenario)
    {
        using var account = FixtureRoot.Create();
        var expected = MakeDefault(account);
        var environment = scenario switch
        {
            "missing" => Path.Combine(account.Root, "missing"),
            "malformed" => "C:\\bad\0path",
            _ => account.DesktopHome,
        };
        var candidates = Locator(account, environment).DiscoverCandidates().Candidates;
        Assert.Contains(candidates, x => x.Kind == RootCandidateKind.DshHome && x.NormalizedPath == expected
            && x.Status == RootCandidateStatus.Valid && x.Sources.Contains(RootCandidateSource.Default));
        var invalid = Assert.Single(candidates, x => x.Sources.Contains(RootCandidateSource.EnvironmentVariable));
        Assert.NotEqual(RootCandidateStatus.Valid, invalid.Status);
        Assert.NotNull(invalid.Reason);
        Assert.Equal(scenario == "missing" ? RootCandidateStatus.Missing : RootCandidateStatus.Invalid, invalid.Status);
        Assert.Null(scenario == "malformed" ? invalid.NormalizedPath : null);
    }

    [Fact]
    public void SameRootDifferentSpelling_MergesAllSources()
    {
        using var account = FixtureRoot.Create();
        MakeDefault(account);
        var result = Locator(account, account.DshHome.ToUpperInvariant()).DiscoverCandidates(
            selectedDshHome: account.DshHome + Path.DirectorySeparatorChar,
            savedDshHome: Path.Combine(account.DshHome, "."));
        var root = Assert.Single(result.Candidates, x => x.Kind == RootCandidateKind.DshHome);
        Assert.Equal(RootCandidateStatus.Valid, root.Status);
        Assert.Equal(account.DshHome, root.NormalizedPath);
        Assert.Equal(4, root.Sources.Count);
        Assert.Equal(4, root.Sources.Distinct().Count());
        Assert.Null(root.Reason);
    }

    [Fact]
    public void DistinctRootsAndKinds_RemainSeparate_NoImplicitSelection()
    {
        using var account = FixtureRoot.Create();
        using var selected = FixtureRoot.Create();
        using var saved = FixtureRoot.Create();
        using var environment = FixtureRoot.Create();
        MakeDefault(account);
        var result = Locator(account, environment.DshHome).DiscoverCandidates(selected.DshHome, saved.DshHome,
            selected.DesktopHome, saved.DesktopHome);
        Assert.Equal(4, result.Candidates.Count(x => x.Kind == RootCandidateKind.DshHome));
        Assert.Equal(3, result.Candidates.Count(x => x.Kind == RootCandidateKind.DesktopHome));
        Assert.Equal(4, result.Candidates.Count(x => x.Kind == RootCandidateKind.DshHome && x.Status == RootCandidateStatus.Valid));
        var samePathKinds = Locator(account).DiscoverCandidates(selectedDesktopHome: account.DshHome).Candidates
            .Where(x => x.NormalizedPath == account.DshHome).ToArray();
        Assert.Equal(2, samePathKinds.Length);
        Assert.Contains(samePathKinds, x => x.Kind == RootCandidateKind.DesktopHome && x.Status == RootCandidateStatus.Invalid);
    }

    [Fact]
    public void LegacyResolve_StillUsesSelectionThenEnvironment_NotCandidateFallback()
    {
        using var account = FixtureRoot.Create();
        using var selected = FixtureRoot.Create();
        MakeDefault(account);
        var environment = Path.Combine(account.Root, "missing-env");
        var locator = Locator(account, environment);
        var roots = locator.Resolve(selected.DshHome, selected.DesktopHome);
        Assert.Equal(selected.DshHome, roots.DshHome);
        Assert.Equal(RootSource.UserSelected, roots.DshProbe.Source);
        roots = locator.Resolve();
        Assert.Equal(environment, roots.DshHome);
        Assert.Equal(RootSource.EnvironmentVariable, roots.DshProbe.Source);
        Assert.Equal(RootProbeStatus.Missing, roots.DshProbe.Status);
        Assert.Contains(locator.DiscoverCandidates().Candidates, x => x.Kind == RootCandidateKind.DshHome && x.Status == RootCandidateStatus.Valid);
    }

    [Fact]
    public void Discovery_IsRepeatable_ReadOnly_AndDoesNotCreateMissingPaths()
    {
        using var account = FixtureRoot.Create();
        MakeDefault(account);
        var sentinel = account.Factory.WriteFile(account.Root, "sentinel.txt", FixtureFactory.Utf8("synthetic"));
        var beforeHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var beforeEntries = Directory.GetFileSystemEntries(account.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var missing = Path.Combine(account.Root, "not-created");
        var locator = Locator(account);
        var first = locator.DiscoverCandidates(savedDshHome: missing).Candidates;
        var second = locator.DiscoverCandidates(savedDshHome: missing).Candidates;
        Assert.Equal(first.Select(x => (x.Kind, x.NormalizedPath, x.Status, x.Reason)), second.Select(x => (x.Kind, x.NormalizedPath, x.Status, x.Reason)));
        Assert.Equal(RootCandidateStatus.Missing, Assert.Single(first, x => x.Sources.Contains(RootCandidateSource.Saved)).Status);
        Assert.False(Directory.Exists(missing));
        Assert.Equal(beforeEntries, Directory.GetFileSystemEntries(account.Root, "*", SearchOption.AllDirectories).OrderBy(x => x));
        Assert.Equal(beforeHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Fact]
    public void ReparseRoot_IsNotAcceptedAsASecondActualRoot()
    {
        using var account = FixtureRoot.Create();
        using var target = FixtureRoot.Create();
        var link = Path.Combine(account.Root, "linked-home");
        try
        {
            Directory.CreateSymbolicLink(link, target.DshHome);
            var result = Locator(account).DiscoverCandidates(selectedDshHome: link, savedDshHome: target.DshHome);
            var candidate = Assert.Single(result.Candidates, x => x.NormalizedPath == link);
            Assert.Equal(RootCandidateStatus.Invalid, candidate.Status);
            Assert.Equal(PathGuard.PathReparseErrorCode, candidate.Reason);
            Assert.Single(result.Candidates, x => x.NormalizedPath == target.DshHome && x.Status == RootCandidateStatus.Valid);
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }
}
