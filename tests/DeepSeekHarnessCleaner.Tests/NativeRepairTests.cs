using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class NativeRepairTests
{
    [Fact]
    public void V0ToolResultWrapper_ShowsToolText_NotUserOrCorruption()
    {
        var text = """{"type":"tool/result","data":{"message":{"role":"user","content":[{"type":"tool-result","toolCallId":"legacy-1","content":[{"type":"text","text":"synthetic V0 tool result"}]}]}}}""";
        var projection = new TranscriptProjector().Project(text);
        Assert.True(projection.IsProjectionComplete);
        var item = Assert.Single(projection.Items);
        Assert.Equal("tool", item.Role);
        Assert.Equal("synthetic V0 tool result", item.Text);
    }

    [Fact]
    public void NormalControlEventsAndToolResults_AreNotCorruption()
    {
        var text = """
            {"type":"request/header","data":{"header":{"private":"do not display"}}}
            {"type":"request/context","data":{}}
            {"type":"todo/write","data":{"todos":[]}}
            {"type":"workspace/changes","data":{"turn":1}}
            {"type":"tool/call","time":1767225600000,"data":{"callId":"call-1","name":"read_file","arguments":"hidden arguments"}}
            {"type":"tool/result","time":1767225601000,"data":{"message":{"role":"tool","toolCallId":"call-1","content":[{"type":"text","text":"synthetic tool result"}]}}}
            """;
        var projection = new TranscriptProjector().Project(text);
        Assert.Equal(0, projection.IncompleteCount);
        Assert.Contains(projection.Items, x => x.Role == "tool" && x.Text.Contains("synthetic tool result", StringComparison.Ordinal));
        Assert.Contains(projection.Items, x => x.Text.Contains("read_file", StringComparison.Ordinal));
        var output = string.Join('\n', projection.Items.Select(x => x.Text));
        Assert.DoesNotContain("hidden arguments", output);
        Assert.DoesNotContain("do not display", output);
    }

    [Fact]
    public void UnknownType_IsUnsupported_NotClaimedAsBrokenData()
    {
        var projection = new TranscriptProjector().Project("""{"type":"future-extension","data":{"secret":"not shown"}}""");
        var item = Assert.Single(projection.Items);
        Assert.True(item.IsPartial);
        Assert.Contains("未支持事件", item.Text);
        Assert.DoesNotContain("not shown", item.Text);
    }

    [Fact]
    public void NativeDelete_SelectedBackupRequiresValidationAndInactiveProcess_KeepsSentinels()
    {
        using var fx = FixtureRoot.Create();
        var selected = fx.Factory.WriteSessionFile("project", "native-delete", "session.v4.jsonl.zstd",
            FixtureFactory.ZstdFrames("""{"type":"session","id":"native-delete","version":4,"createdAt":1767225600000}""" + "\n"));
        var keep = fx.Factory.WriteValidV4Session();
        var attachment = fx.Factory.WriteAttachment("keep-attachment");
        var sentinel = fx.Factory.WriteFile(fx.Root, "outside.bin", [4, 5, 6]);
        var protectedHashes = new[] { keep, attachment, sentinel }.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var session = new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions.Single(x => x.SessionId == "native-delete");
        var actions = new ActionsPresenter(() => []);
        var plan = actions.BuildSessionPlan(fx.SessionsRoot, [session]).Plan!;
        Assert.Single(plan.Targets);
        Assert.NotNull(plan.Targets[0].ExpectedSha256);
        var manifest = actions.BuildSessionManifest(fx.DshHome, [session]).Manifest!;
        var executor = new CleanupExecutor(new HarnessProcessProbe(() => []));
        Assert.True(executor.Execute(plan with { RequiresBackup = true }, null, null, fx.SessionsRoot, true).Aborted);
        var zip = Path.Combine(fx.Root, "backup.zip");
        var backup = new BackupService().CreateBackup(manifest, zip).Backup!;
        var active = new CleanupExecutor(new HarnessProcessProbe(() => [new ProcessSnapshot(1, "DeepSeek Harness", @"C:\Synthetic\DeepSeek Harness.exe", true)]));
        Assert.True(active.Execute(plan, manifest, backup, fx.SessionsRoot, true).Aborted);
        Assert.True(File.Exists(selected));
        Assert.True(executor.Execute(plan, manifest, backup, fx.SessionsRoot, true).AllSucceeded);
        Assert.False(File.Exists(selected));
        foreach (var pair in protectedHashes) Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key)));
    }

    [Theory]
    [InlineData("new-generation")]
    [InlineData("unknown-file")]
    [InlineData("modified")]
    [InlineData("malformed")]
    [InlineData("version-mismatch")]
    [InlineData("future-generation")]
    public void PreparationRefusesChangedOrUnverifiedWholeSession(string scenario)
    {
        using var fx = FixtureRoot.Create();
        var header = """{"type":"session","id":"safe","version":4,"createdAt":1767225600000}""";
        var path = fx.Factory.WriteSessionFile("project", "safe", scenario == "future-generation" ? "session.v7.jsonl" : "session.v4.jsonl", FixtureFactory.Utf8(
            (scenario == "version-mismatch" ? header.Replace("\"version\":4", "\"version\":0") : header)
            + "\n" + (scenario == "malformed" ? "{broken\n" : "")));
        var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions);
        if (scenario == "new-generation")
            fx.Factory.WriteSessionFile("project", "safe", "session.jsonl", FixtureFactory.Utf8(header.Replace("\"version\":4", "\"version\":0") + "\n"));
        if (scenario == "unknown-file") File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "unrecognized.bin"), "preserve");
        if (scenario == "modified") File.AppendAllText(path, "{\"type\":\"future\"}\n");
        var result = new ActionsPresenter(() => []).BuildSessionPlan(fx.SessionsRoot, [session]);
        Assert.Empty(result.Plan!.Targets);
        Assert.NotEmpty(result.Exclusions);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMultiGeneration_BackupCoversAll_ChangedFileIsSkipped(bool changeAfterBackup)
    {
        using var fx = FixtureRoot.Create();
        foreach (var generation in new[] { 0, 4 })
            fx.Factory.WriteSessionFile("project", "multi", generation == 0 ? "session.jsonl" : "session.v4.jsonl",
                FixtureFactory.Utf8("{\"type\":\"session\",\"id\":\"multi\",\"version\":" + generation + ",\"createdAt\":1767225600000}\n"));
        var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions);
        var actions = new ActionsPresenter(() => []);
        var plan = actions.BuildSessionPlan(fx.SessionsRoot, [session]).Plan!;
        Assert.Equal(2, plan.Targets.Count);
        var manifest = actions.BuildSessionManifest(fx.DshHome, [session]).Manifest!;
        Assert.Equal(2, manifest.Entries.Count);
        var backup = new BackupService().CreateBackup(manifest, Path.Combine(fx.Root, "all-generations.zip")).Backup!;
        var changed = plan.Targets[0];
        if (changeAfterBackup)
        {
            var bytes = File.ReadAllBytes(changed.AbsolutePath);
            bytes[0] = (byte)'[';
            File.WriteAllBytes(changed.AbsolutePath, bytes);
            File.SetLastWriteTimeUtc(changed.AbsolutePath, changed.ExpectedWriteUtc.UtcDateTime);
        }
        var result = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, manifest, backup, fx.SessionsRoot, true);
        if (changeAfterBackup)
        {
            Assert.Contains(result.Outcomes, x => x.AbsolutePath == changed.AbsolutePath && x.Reason == "CHANGED_SHA256");
            Assert.True(File.Exists(changed.AbsolutePath));
            Assert.Equal(1, result.DeletedCount);
        }
        else
        {
            Assert.True(result.AllSucceeded);
            Assert.Equal(2, result.DeletedCount);
            Assert.All(plan.Targets, x => Assert.False(File.Exists(x.AbsolutePath)));
        }
    }
}
