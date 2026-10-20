using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class FeedbackRepairTests
{
    [Theory]
    [InlineData("archived", true)]
    [InlineData("empty", false)]
    [InlineData("missing", null)]
    [InlineData("field-missing", null)]
    [InlineData("bad-json", null)]
    [InlineData("future-version", null)]
    [InlineData("wrong-domain", null)]
    [InlineData("not-initialized", null)]
    [InlineData("invalid-id", null)]
    [InlineData("duplicate-field", null)]
    [InlineData("pinned-conflict", null)]
    public void ArchiveColumn_UsesVerifiedRegistryOrUnknown(string scenario, bool? expected)
    {
        using var fx = FixtureRoot.Create();
        WriteSession(fx);
        var json = """{"unit":{"name":"workspace","version":2},"global":{"initialized":true,"archivedSessionIds":["feedback"]},"tables":{"workspaces":[]}}""";
        json = scenario switch
        {
            "empty" => json.Replace("[\"feedback\"]", "[]"),
            "field-missing" => json.Replace(",\"archivedSessionIds\":[\"feedback\"]", ""),
            "bad-json" => "{",
            "future-version" => json.Replace("\"version\":2", "\"version\":3"),
            "wrong-domain" => json.Replace("\"workspace\"", "\"other\""),
            "not-initialized" => json.Replace("true", "false"),
            "invalid-id" => json.Replace("[\"feedback\"]", "[\"feedback\",4]"),
            "duplicate-field" => json.Replace("\"archivedSessionIds\":[\"feedback\"]", "\"archivedSessionIds\":[],\"archivedSessionIds\":[\"feedback\"]"),
            "pinned-conflict" => json.Replace("\"initialized\":true", "\"initialized\":true,\"pinnedSessionIds\":[\"feedback\"]"),
            _ => json,
        };
        if (scenario != "missing") File.WriteAllText(Path.Combine(fx.StoragesRoot, "workspace.json"), json);
        var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions);
        Assert.Equal(expected, session.IsArchived);
        Assert.Equal(ParseStatus.Valid, session.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoBackupDeletion_StillEnforcesAllSafetyGates(bool attachment)
    {
        foreach (var scenario in new[] { "success", "cancel", "canceled-token", "active", "unknown", "changed", "same-size-changed", "wrong-root", "backup-selected" })
        {
            using var fx = FixtureRoot.Create();
            var target = attachment ? fx.Factory.WriteAttachment("target") : WriteSession(fx);
            var sentinel = fx.Factory.WriteFile(fx.Root, "outside.bin", [7, 8, 9]);
            var keep = fx.Factory.WriteAttachment("unselected");
            var before = new[] { sentinel, keep }.ToDictionary(x => x, Hash);
            var actions = new ActionsPresenter(() => []);
            var session = Assert.Single(new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions.DefaultIfEmpty(
                new SessionSummary(null, null, null, null, DateOrigin.Unknown, [], ParseStatus.Valid)));
            var plan = attachment
                ? new AttachmentCleanupSelection().Select(fx.DshHome, new AttachmentInventoryService().Scan(fx.DshHome, []).Items.Select(x => x.Candidate).ToArray(), [target]).Plan!
                : actions.BuildSessionPlan(fx.SessionsRoot, [session]).Plan!;
            var root = attachment ? fx.DshHome : fx.SessionsRoot;
            Assert.False(plan.RequiresBackup);
            Assert.Single(plan.Targets);
            if (scenario == "changed") File.AppendAllText(target, "changed");
            if (scenario == "same-size-changed")
            {
                var mtime = File.GetLastWriteTimeUtc(target);
                var bytes = File.ReadAllBytes(target);
                bytes[0] ^= 1;
                File.WriteAllBytes(target, bytes);
                File.SetLastWriteTimeUtc(target, mtime);
            }
            if (scenario == "backup-selected") plan = plan with { RequiresBackup = true };
            var probe = scenario switch
            {
                "active" => new HarnessProcessProbe(() => [new ProcessSnapshot(1, "DeepSeek Harness", @"C:\Synthetic\DeepSeek Harness.exe", true)]),
                "unknown" => new HarnessProcessProbe(() => throw new UnauthorizedAccessException()),
                _ => new HarnessProcessProbe(() => []),
            };
            var result = new CleanupExecutor(probe).Execute(plan, null, null, scenario == "wrong-root" ? fx.Root : root, scenario != "cancel",
                new CancellationToken(scenario == "canceled-token"));
            Assert.Equal(scenario == "success", result.AllSucceeded);
            Assert.Equal(scenario != "success", File.Exists(target));
            foreach (var file in before) Assert.Equal(file.Value, Hash(file.Key));
            var report = actions.BuildReport(plan, null, result, Path.Combine(fx.Root, "not-created.zip"));
            Assert.Null(report.BackupZipPath);
            Assert.False(File.Exists(Path.Combine(fx.Root, "not-created.zip")));
        }
    }

    [Fact]
    public void ArchiveReader_RejectsReparseStorageWithoutFollowingIt()
    {
        using var fx = FixtureRoot.Create();
        var outside = Path.Combine(fx.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "workspace.json"), """{"unit":{"name":"workspace","version":2},"global":{"initialized":true,"archivedSessionIds":[]}}""");
        var before = Hash(Path.Combine(outside, "workspace.json"));
        var alternateHome = Path.Combine(fx.Root, "other-home");
        Directory.CreateDirectory(alternateHome);
        var link = Path.Combine(alternateHome, "storages");
        CreateJunction(link, outside);
        try
        {
            Assert.Null(new WorkspaceArchiveReader().Read(alternateHome));
            Assert.Equal(before, Hash(Path.Combine(outside, "workspace.json")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void NoBackupDeletion_RejectsTargetDirectoryReplacedByJunction()
    {
        using var fx = FixtureRoot.Create();
        var target = fx.Factory.WriteAttachment("target");
        var item = new AttachmentInventoryService().Scan(fx.DshHome, []).Items.Single();
        var plan = new AttachmentCleanupSelection().Select(fx.DshHome, [item.Candidate], [target]).Plan!;
        var outside = Path.Combine(fx.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, Path.GetFileName(target));
        File.WriteAllText(sentinel, "protected outside");
        var before = Hash(sentinel);
        var original = fx.AttachmentsRoot + "-original";
        Directory.Move(fx.AttachmentsRoot, original); // Fresh synthetic fixture only.
        CreateJunction(fx.AttachmentsRoot, outside);
        try
        {
            var result = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, null, null, fx.DshHome, true);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(before, Hash(sentinel));
            Assert.True(File.Exists(Path.Combine(original, Path.GetFileName(target))));
        }
        finally { Directory.Delete(fx.AttachmentsRoot); }
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Theory]
    [InlineData("objects", "image", "photo.png", "图片（image/png）")]
    [InlineData("file-objects", "file", "report.pdf", "文件")]
    [InlineData("files", "file", "report.pdf", "文件")]
    public void AttachmentDisplay_UsesVerifiedNamesTypesAndSessions(string layout, string blockType, string name, string expectedType)
    {
        using var fx = FixtureRoot.Create();
        byte[] bytes = [5, 6, 7];
        var hash = Hash(bytes);
        var relative = Path.Combine("v1", layout, hash[..2], hash);
        if (layout == "files") relative = Path.Combine(relative, name);
        var target = fx.Factory.WriteFile(fx.AttachmentsRoot, relative, bytes);
        var attachment = new { attachmentId = "sha256:" + hash, name, mediaType = "image/png", bytes = bytes.Length };
        var text = JsonSerializer.Serialize(new { type = "user/message", data = new { role = "user", content = new[] { new { type = blockType, attachment } } } });
        WriteSession(fx, text);
        var sessions = new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions;
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, sessions).Items);
        Assert.Equal(target, item.Candidate.FullPath);
        Assert.Equal(name, item.DisplayName);
        Assert.Equal(expectedType, item.TypeLabel);
        Assert.Equal(["feedback"], item.ReferencingSessionIds);
    }

    [Fact]
    public void AttachmentDisplay_UnknownAndUnsafeNamesAreNotInvented()
    {
        var json = """{"type":"user/message","data":{"role":"user","content":[{"type":"image","attachment":{"attachmentId":"a","name":"C:\\private\\name.png","mediaType":"bad/type"}}]}}""";
        var evidence = Assert.Single(new AttachmentIndex().Build([new SessionAttachmentSource("s", json, ParseStatus.Partial)], ["a"]).Attachments);
        Assert.Empty(evidence.DisplayNames);
        Assert.Equal(["图片（格式未知）"], evidence.TypeLabels);
        using var fx = FixtureRoot.Create();
        byte[] bytes = [1, 2];
        var hash = Hash(bytes);
        fx.Factory.WriteFile(fx.AttachmentsRoot, Path.Combine("v1", "objects", hash[..2], hash), bytes);
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        Assert.Equal("名称未知", item.DisplayName);
        Assert.Equal("类型未知", item.TypeLabel);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, item.Candidate.EvidenceStatus);
    }

    private static string WriteSession(FixtureRoot fx, string events = "") => fx.Factory.WriteSessionFile("project", "feedback", "session.v4.jsonl",
        FixtureFactory.Utf8("""{"type":"session","id":"feedback","version":4,"createdAt":1767225600000}""" + "\n" + (events.Length > 0 ? events + "\n" : "")));

    private static string Hash(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
