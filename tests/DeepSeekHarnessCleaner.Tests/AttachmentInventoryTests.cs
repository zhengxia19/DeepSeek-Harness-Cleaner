using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class AttachmentInventoryTests
{
    [Fact]
    public void NativeObjectMapping_SharedReference_NoOrphanGuess_ExplicitBackupDelete()
    {
        using var fx = FixtureRoot.Create();
        byte[] bytes = [1, 2, 3, 4];
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var target = fx.Factory.WriteFile(fx.AttachmentsRoot, Path.Combine("v1", "objects", sha[..2], sha), bytes);
        var keep = fx.Factory.WriteAttachment("keep");
        foreach (var id in new[] { "a", "b" })
            fx.Factory.WriteSessionFile("project", id, "session.v4.jsonl", FixtureFactory.Utf8(
                "{\"type\":\"session\",\"version\":4,\"id\":\"" + id + "\",\"createdAt\":1767225600000}\n"
                + "{\"type\":\"user/message\",\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"attachment\":{\"attachmentId\":\"sha256:" + sha + "\"}}]}}\n"));
        var sessions = new InventoryService(new UiScanSource()).Scan(fx.SessionsRoot).Sessions;
        var inventory = new AttachmentInventoryService().Scan(fx.DshHome, sessions);
        var item = inventory.Items.Single(x => x.Candidate.FullPath == target);
        Assert.Equal(AttachmentEvidenceStatus.Referenced, item.Candidate.EvidenceStatus);
        Assert.Equal(2, item.ReferencingSessionIds.Count);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, inventory.Items.Single(x => x.Candidate.FullPath == keep).Candidate.EvidenceStatus);
        var selection = new AttachmentCleanupSelection();
        Assert.Empty(selection.Select(fx.DshHome, inventory.Items.Select(x => x.Candidate).ToArray(), []).Plan!.Targets);
        var plan = selection.Select(fx.DshHome, inventory.Items.Select(x => x.Candidate).ToArray(), [target]).Plan!;
        var manifest = new BackupManifestBuilder().Build(fx.DshHome, [], [target]).Manifest!;
        var keepHash = SHA256.HashData(File.ReadAllBytes(keep));
        var sentinel = fx.Factory.WriteFile(fx.Root, "sentinel.bin", [7, 8, 9]);
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var executor = new CleanupExecutor(new HarnessProcessProbe(() => []));
        Assert.True(executor.Execute(plan with { RequiresBackup = true }, null, null, fx.DshHome, true).Aborted);
        var backup = new BackupService().CreateBackup(manifest, Path.Combine(fx.Root, "attachments.zip")).Backup!;
        Assert.True(executor.Execute(plan, manifest, backup, fx.DshHome, true).AllSucceeded);
        Assert.False(File.Exists(target));
        Assert.Equal(keepHash, SHA256.HashData(File.ReadAllBytes(keep)));
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.All(sessions.SelectMany(s => s.Generations), x => Assert.True(File.Exists(x.FullPath)));
    }

    [Fact]
    public void IncorrectDigestIsNeverMappedToAReference()
    {
        using var fx = FixtureRoot.Create();
        var sha = new string('a', 64);
        fx.Factory.WriteFile(fx.AttachmentsRoot, Path.Combine("v1", "objects", "aa", sha), [5]);
        var item = Assert.Single(new AttachmentInventoryService().Scan(fx.DshHome, []).Items);
        Assert.Null(item.AttachmentId);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, item.Candidate.EvidenceStatus);
    }

    [Fact]
    public void JunctionInsideAttachments_IsNotFollowed_OutsideHashPreserved()
    {
        using var fx = FixtureRoot.Create();
        var outside = Path.Combine(fx.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.bin");
        File.WriteAllBytes(sentinel, [8, 9]);
        var before = SHA256.HashData(File.ReadAllBytes(sentinel));
        var junction = Path.Combine(fx.AttachmentsRoot, "escape-link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = new AttachmentInventoryService().Scan(fx.DshHome, []);
            Assert.Empty(result.Items);
            Assert.Contains(PathGuard.PathReparseErrorCode, result.Issues);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(sentinel)));
        }
        finally { Directory.Delete(junction); }
    }
}
