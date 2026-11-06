using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceProductionGateTests
{
    private static StorageGateFixture Create(string home)
    {
        var fixture = new StorageGateFixture(home);
        fixture.Initialize();
        return fixture;
    }

    [Fact]
    public void ComposesValidBindingsAndFingerprintsAllInputsRatherThanWhitelistingPersonalConfig()
    {
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        var first = fixture.Read();
        Assert.Equal(64, first?.Length);
        fixture.Local[fixture.UserPatch] = Encoding.UTF8.GetBytes("- id: unrelated\n  config: {model: arbitrary, token: private}");
        var second = fixture.Read();
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(second, fixture.Read());
        fixture.Local[Path.Combine(root.DshHome, "cordis.patch.yml")] = Encoding.UTF8.GetBytes("- id: storage-domain\n  config: {backend: other}");
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void UsesLocalBundleOnlyIfInstallationDoesNotSupplyIt()
    {
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        var prefix = Path.Combine(root.DshHome, "profiles", "desktop", "node_modules", "test-bundle");
        fixture.Local[Path.Combine(prefix, "package.json")] = fixture.Packages[StorageGateFixture.BundleManifest];
        fixture.Local[Path.Combine(prefix, "cordis.patch.yml")] = Encoding.UTF8.GetBytes("[]");
        Assert.NotNull(fixture.Read());
        fixture.Packages.Remove(StorageGateFixture.BundleManifest);
        Assert.Null(fixture.Read());
        fixture.Local[Path.Combine(prefix, "cordis.patch.yml")] = fixture.Packages[StorageGateFixture.BundlePatch];
        Assert.NotNull(fixture.Read());
    }

    [Theory]
    [InlineData("../outside.yml")]
    [InlineData("C:/outside.yml")]
    [InlineData("folder\\outside.yml")]
    [InlineData("./missing.yml")]
    public void RejectsEscapingOrMissingDeclaredPatch(string path)
    {
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        fixture.Packages[StorageGateFixture.BundleManifest] = StorageGateFixture.Json(new { name = "test-bundle", dsh = new { bundle = new { patch = path } } });
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData("{\"name\":\"test-bundle\",\"engines\":{\"dsh\":\"9.0.0\"}}")]
    [InlineData("{\"name\":\"other\"}")]
    [InlineData("{\"name\":\"test-bundle\",\"name\":\"other\"}")]
    [InlineData("[1]")]
    [InlineData("broken")]
    public void RejectsUnknownOrAmbiguousManifest(string json)
    {
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        fixture.Packages[StorageGateFixture.BundleManifest] = Encoding.UTF8.GetBytes(json);
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void InspectsEveryDeclaredPatchAndCoreManifest()
    {
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        fixture.Packages[StorageGateFixture.BundleManifest] = StorageGateFixture.Json(new { name = "test-bundle", dsh = new { bundle = new { patch = new[] { "./cordis.patch.yml", "./second.yml" } } } });
        fixture.Packages["dsh/node_modules/test-bundle/second.yml"] = Encoding.UTF8.GetBytes("- id: workspace\n  disabled: true");
        Assert.Null(fixture.Read());
        fixture.Packages["dsh/node_modules/test-bundle/second.yml"] = Encoding.UTF8.GetBytes("[]");
        Assert.NotNull(fixture.Read());
        fixture.Packages["dsh/node_modules/@deepseek-ai/dsh-workspace/package.json"] = StorageGateFixture.Json(new { name = "@deepseek-ai/dsh-workspace", version = "999" });
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void ProductionEntryDoesNotTreatSyntheticProfileAsInstalledReleaseEvidence()
    {
        using var root = FixtureRoot.Create();
        Assert.Null(WorkspaceProductionGate.Read(root.DshHome));
    }

    [Fact]
    public void DuplicateKeysAreJudgedIdenticallyByWalkerAndGateEntries()
    {
        // 同一含重复键 JSON：walker 入口报出重复位置，gate 入口（经 Document→DuplicateField）拒绝绑定。
        const string json = "{\"name\":\"test-bundle\",\"dsh\":{\"bundle\":{\"patch\":\"./cordis.patch.yml\"}},\"dsh\":{\"bundle\":{\"patch\":\"./cordis.patch.yml\"}}}";
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("/dsh", WorkspaceJson.DuplicateField(document.RootElement));
        using var root = FixtureRoot.Create();
        var fixture = Create(root.DshHome);
        fixture.Packages[StorageGateFixture.BundleManifest] = Encoding.UTF8.GetBytes(json);
        Assert.Null(fixture.Read());
    }
}
