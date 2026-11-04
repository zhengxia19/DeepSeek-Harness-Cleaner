using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceBackupTests
{
    private static readonly WorkspaceBackupService Service = new(new HarnessProcessProbe(() => []));

    private static WorkspaceMetadataPlan Plan(FixtureRoot root)
    {
        var fixture = new WorkspaceFixture(root);
        var path = fixture.Write("valid");
        File.WriteAllBytes(path, new byte[] { 0xef, 0xbb, 0xbf }.Concat(File.ReadAllBytes(path)).Concat(new byte[] { 32, 13, 10 }).ToArray());
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var plan = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), plan);
        var execution = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, null, null, root.SessionsRoot, true);
        Assert.False(execution.Aborted);
        var facts = execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath,
            x.Status == ItemStatus.Deleted ? WorkspacePhysicalStatus.Deleted : WorkspacePhysicalStatus.Failed)).ToArray();
        var proposal = new WorkspaceMetadataProposal().Build(plan, before, facts, new PostCleanupAuditor().Audit(root.SessionsRoot, plan, before));
        Assert.Equal(WorkspaceSyncState.Proposed, proposal.State);
        return proposal;
    }

    private static string Destination(FixtureRoot root) => Directory.CreateDirectory(Path.Combine(root.Root, "metadata-backups")).FullName;

    [Fact]
    public void RawBytesAndIdentityAreVerified_IndependentOfOptionalSessionZip_AndIdempotent()
    {
        using var root = FixtureRoot.Create();
        var plan = Plan(root);
        var raw = File.ReadAllBytes(plan.WorkspacePath);
        var result = Service.Create(plan, Destination(root));
        Assert.True(result.Success, result.ErrorCode);
        var backup = result.Backup!;
        Assert.Equal(raw, File.ReadAllBytes(backup.RawPath));
        Assert.Equal(plan.SourceSha256, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
        Assert.Equal(plan.WorkspacePath, backup.SourcePath);
        Assert.True(File.Exists(backup.ManifestPath));
        Assert.True(File.Exists(backup.VerificationPath));
        Assert.True(Service.Verify(plan, backup).Success);
        var repeat = Service.Create(plan, backup.BackupRoot);
        Assert.Equal(backup, repeat.Backup);
        Assert.Equal(raw, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Empty(Directory.GetFiles(root.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Single(WorkspaceMetadataPlan.ProductionWriteContractIds);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("manifest")]
    [InlineData("verification")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("schema")]
    [InlineData("source-path")]
    [InlineData("operation")]
    public void UnverifiableArtifactsNeverReturnBackupEvidence(string fault)
    {
        using var root = FixtureRoot.Create();
        var plan = Plan(root);
        var backup = Service.Create(plan, Destination(root)).Backup!;
        var original = File.ReadAllBytes(plan.WorkspacePath);
        if (fault == "raw") File.AppendAllText(backup.RawPath, "changed");
        if (fault == "manifest") File.WriteAllText(backup.ManifestPath, "{");
        if (fault == "verification") File.WriteAllText(backup.VerificationPath, "{");
        if (fault == "missing") File.Delete(backup.RawPath);
        if (fault == "duplicate") File.WriteAllText(backup.VerificationPath, "{\"schemaVersion\":1,\"schemaVersion\":1}");
        if (fault is "unknown" or "schema" or "source-path")
        {
            var json = JsonNode.Parse(File.ReadAllText(backup.ManifestPath))!;
            if (fault == "unknown") json["extra"] = true;
            if (fault == "schema") json["schemaVersion"] = 99;
            if (fault == "source-path") json["sourcePath"] = Path.Combine(root.Root, "unrelated.json");
            File.WriteAllText(backup.ManifestPath, json.ToJsonString());
        }
        if (fault == "operation") plan = plan with { CleanupPlanId = Guid.NewGuid() };
        Assert.False(Service.Verify(plan, backup).Success);
        Assert.False(Service.Create(plan, backup.BackupRoot).Success);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
    }

    [Fact]
    public void SourceChangeBlocksCreation_ButVerifiedOriginalSupportsSyntheticRecovery()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var plan = Plan(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var destination = Destination(root);
        File.AppendAllText(plan.WorkspacePath, " ");
        Assert.False(Service.Create(plan, destination).Success);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        File.WriteAllBytes(plan.WorkspacePath, original);
        var backup = Service.Create(plan, destination).Backup!;
        File.WriteAllText(plan.WorkspacePath, "synthetic interrupted replacement");
        Assert.False(Service.Create(plan, destination).Success);
        Assert.True(Service.Verify(plan with { State = WorkspaceSyncState.RecoveryRequired }, backup).Success);
        // Recovery rehearsal only: fixture-owned source, rechecked evidence, no production restore API.
        Assert.StartsWith(root.Root + Path.DirectorySeparatorChar, plan.WorkspacePath);
        Assert.True(new PathGuard().Check(root.DshHome, plan.WorkspacePath).IsAllowed);
        Assert.True(new HarnessProcessProbe(() => []).Probe().AllowsWrite);
        var restore = File.ReadAllBytes(backup.RawPath);
        Assert.Equal(plan.SourceSha256, Convert.ToHexString(SHA256.HashData(restore)).ToLowerInvariant());
        File.WriteAllBytes(plan.WorkspacePath, restore);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        Assert.True(Service.Verify(plan, backup).Success);
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("relative")]
    [InlineData("incomplete")]
    [InlineData("canceled")]
    [InlineData("active")]
    [InlineData("unknown-process")]
    public void InvalidConditionsDoNotCreateOrOverwriteArtifacts(string fault)
    {
        using var root = FixtureRoot.Create();
        var plan = Plan(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var destination = Destination(root);
        var service = Service;
        if (fault == "inside") destination = root.StoragesRoot;
        if (fault == "relative") destination = "relative-backups";
        if (fault == "active") service = new(new HarnessProcessProbe(() => [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]));
        if (fault == "unknown-process") service = new(new HarnessProcessProbe(() => throw new InvalidOperationException()));
        string? partial = null;
        if (fault == "incomplete")
        {
            var op = Directory.CreateDirectory(Path.Combine(destination, "workspace-sync-" + plan.OperationId.ToString("N"))).FullName;
            partial = Path.Combine(op, "workspace.original.json");
            File.WriteAllText(partial, "partial preserved");
        }
        var result = service.Create(plan, destination, fault == "canceled" ? new CancellationToken(true) : default);
        Assert.False(result.Success);
        Assert.Null(result.Backup);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        if (partial is not null) Assert.Equal("partial preserved", File.ReadAllText(partial));
        else Assert.Empty(Directory.GetFileSystemEntries(Destination(root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReparseDestinationOrArtifactIsRejected_OutsideSentinelPreserved(bool artifact)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var plan = Plan(root);
        var destination = Destination(root);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.json", FixtureFactory.Utf8("protected"));
        var hash = SHA256.HashData(File.ReadAllBytes(sentinel));
        if (artifact)
        {
            var backup = Service.Create(plan, destination).Backup!;
            File.Delete(backup.RawPath);
            File.CreateSymbolicLink(backup.RawPath, sentinel);
            Assert.False(Service.Verify(plan, backup).Success);
        }
        else
        {
            var link = Path.Combine(root.Root, "linked-backup");
            Directory.CreateSymbolicLink(link, outside.Root);
            Assert.False(Service.Create(plan, link).Success);
        }
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.Single(Directory.GetFileSystemEntries(outside.Root), x => Path.GetFileName(x) == "sentinel.json");
    }

    [Fact]
    public void ExclusiveFileLockAndForgedEvidenceCannotPassVerification()
    {
        using var root = FixtureRoot.Create();
        var plan = Plan(root);
        var backup = Service.Create(plan, Destination(root)).Backup!;
        using (var locked = new FileStream(backup.RawPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Service.Verify(plan, backup).Success);
            Assert.False(Service.Create(plan, backup.BackupRoot).Success);
        }
        Assert.True(Service.Verify(plan, backup).Success);
        Assert.False(Service.Verify(plan, backup with { ManifestSha256 = new string('0', 64) }).Success);
        Assert.False(Service.Verify(plan, backup with { RawPath = plan.WorkspacePath }).Success);
        Assert.False(Service.Create(plan with { State = WorkspaceSyncState.Blocked }, backup.BackupRoot).Success);
    }
}
