using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

// Structure verified read-only against local logs and the upstream persistence catalog.
// All values below are synthetic. These are log events, not a replayed Harness surface.
public sealed class NativeSessionTests
{
    private const string Header = """{"type":"session","id":"native-001","version":4,"createdAt":1767225600000,"cwd":"D:\\Synthetic"}""";
    private const string User = """{"type":"user/message","seq":1,"time":1767225601000,"data":{"role":"user","content":[{"type":"text","text":"synthetic prompt"}]}}""";
    private const string Assistant = """{"type":"assistant/message","seq":2,"time":1767225602000,"data":{"message":{"role":"assistant","content":[{"type":"reasoning","text":"hidden reasoning"},{"type":"text","text":"synthetic answer"},{"type":"tool-call","arguments":"secret arguments"}]}}}""";
    private const string Title = """{"type":"session/title","data":{"title":"Synthetic title"}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeScanAndPreview_VerifiedFormatOffersWholeSessionPlan(bool compressed)
    {
        using var fx = FixtureRoot.Create();
        var text = string.Join('\n', Header, Title, User, Assistant) + "\n";
        var path = fx.Factory.WriteSessionFile("project", "native-001",
            "session.v4.jsonl" + (compressed ? ".zstd" : ""),
            compressed ? FixtureFactory.ZstdFrames(text) : FixtureFactory.Utf8(text));
        var before = File.ReadAllBytes(path);
        var inventory = new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot);
        var session = Assert.Single(inventory.Sessions);
        Assert.False(session.IsAmbiguous);
        Assert.Equal("native-001", session.SessionId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000), session.CreatedAt);
        Assert.Equal("Synthetic title", session.Title);
        Assert.Equal(ParseStatus.Valid, session.Status);
        var plan = new CleanupPlanner().Build(fx.SessionsRoot, [session], []);
        Assert.Single(plan.Plan!.Targets);
        Assert.False(plan.Plan.RequiresBackup);
        var preview = await new PreviewPresenter().PreviewAsync(fx.SessionsRoot, session, CancellationToken.None);
        var items = preview.Result.Projection!.Items.Where(x => x.Role is "user" or "assistant").ToArray();
        Assert.Equal(new[] { "synthetic prompt", "synthetic answer" }, items.Select(x => x.Text));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225601000), items[0].At);
        var rendered = PreviewPresenter.Render(preview);
        Assert.DoesNotContain("hidden reasoning", rendered);
        Assert.DoesNotContain("secret arguments", rendered);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    public void NativeHeader_ReadsVerifiedVersions(string version)
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile("project", "native-001", version == "0" ? "session.jsonl" : "session.v4.jsonl",
            FixtureFactory.Utf8(Header.Replace("\"version\":4", "\"version\":" + version)));
        var result = new SessionHeaderReader().Read(path, "native-001");
        Assert.NotNull(result.Header);
        Assert.True(result.IdentityMatches);
        Assert.True(result.AllowsAutoCleanup);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("253402300800000")]
    [InlineData("\"2026-01-01T00:00:00Z\"")]
    [InlineData("null")]
    public void NativeHeader_InvalidMillisecondsAreRejected(string timestamp)
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile("project", "native-001", "session.v4.jsonl",
            FixtureFactory.Utf8(Header.Replace("1767225600000", timestamp)));
        var result = new SessionHeaderReader().Read(path, "native-001");
        Assert.Null(result.Header);
        Assert.False(result.AllowsAutoCleanup);
        Assert.NotEqual(false, result.IdentityMatches); // Missing metadata is not an identity conflict.
    }

    [Fact]
    public void NativeHeader_TrueIdentityMismatchStillBlocks()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteSessionFile("project", "different-id", "session.v4.jsonl", FixtureFactory.Utf8(Header));
        var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions);
        Assert.True(session.IsAmbiguous);
        Assert.Null(session.SessionId);
    }

    [Theory]
    [InlineData("\"4\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("5")]
    [InlineData("3")]
    public void NativeHeader_UnverifiedOrMalformedVersionIsRejected(string version)
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile("project", "native-001", "session.v4.jsonl",
            FixtureFactory.Utf8(Header.Replace("\"version\":4", "\"version\":" + version)));
        var result = new SessionHeaderReader().Read(path, "native-001");
        Assert.Null(result.Header);
        Assert.False(result.AllowsAutoCleanup);
    }

    [Fact]
    public void NativeProjection_UnknownBlocksAndRolesAreExplicit_NoPayloadLeak()
    {
        var input = """{"type":"user/message","time":1767225600000,"data":{"role":"assistant","content":[{"type":"text","text":"wrong role"}]}}""" + "\n"
            + """{"type":"user/message","data":{"role":"user","content":[{"type":"image","attachment":{"data":"private image"}},{"type":"future","text":"unknown payload"}]}}""";
        var result = new TranscriptProjector().Project(input);
        Assert.False(result.IsProjectionComplete);
        Assert.All(result.Items, x => Assert.True(x.IsPartial));
        var output = string.Join('\n', result.Items.Select(x => x.Text));
        Assert.DoesNotContain("wrong role", output);
        Assert.DoesNotContain("private image", output);
        Assert.DoesNotContain("unknown payload", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeNativePreview_PreservesFirstCompleteMessages_ReportsLimit(bool compressed)
    {
        using var fx = FixtureRoot.Create();
        var text = string.Join('\n', Header, User) + "\n"
            + "{\"type\":\"future\",\"padding\":\"" + new string('x', 9 * 1024 * 1024) + "\"}\n";
        fx.Factory.WriteSessionFile("project", "native-001", "session.v4.jsonl" + (compressed ? ".zstd" : ""),
            compressed ? FixtureFactory.ZstdFrames(text) : FixtureFactory.Utf8(text));
        var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions);
        var preview = await new PreviewPresenter().PreviewAsync(fx.SessionsRoot, session, CancellationToken.None);
        Assert.Contains(preview.Result.Projection!.Items, x => x.Role == "user" && x.Text == "synthetic prompt");
        Assert.NotEmpty(preview.Warnings);
        Assert.DoesNotContain(preview.Result.Projection.Items, x => x.Text.Contains("无效 JSON", StringComparison.Ordinal));
    }
}
