using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class PostCleanupAuditTests
{
    private static (CleanupPlan Plan, PreCleanupAudit Audit) Prepare(FixtureRoot root)
    {
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var plan = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        return (plan, new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), plan));
    }

    private static CleanupExecutionResult Delete(FixtureRoot root, CleanupPlan plan) =>
        new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, null, null, root.SessionsRoot, true);

    [Fact]
    public void SuccessfulPhysicalDelete_StillShowsMetadataResidueAndRetainedAttachmentsAreNotFailures()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var retained = root.Factory.WriteAttachment("retained");
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var beforeHash = new[] { fixture.WorkspacePath, retained, sentinel }.ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(x)));
        var prepared = Prepare(root);
        var execution = Delete(root, prepared.Plan);
        Assert.True(execution.AllSucceeded);
        var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        var session = Assert.Single(post.Sessions);
        Assert.Equal(SessionCleanupState.MetadataResidue, session.State);
        Assert.Equal(2, session.WorkspaceReferenceCount); // Archive + workspace member, freshly reread.
        Assert.All(post.Targets, x => Assert.Equal(TargetPresence.Absent, x.Presence));
        Assert.Contains("元数据残留", PostCleanupAuditText.Summary(post));
        Assert.DoesNotContain("文件残留", PostCleanupAuditText.Summary(post));
        Assert.True(post.HasUnknowns);
        foreach (var file in beforeHash) Assert.Equal(file.Value, SHA256.HashData(File.ReadAllBytes(file.Key)));
    }

    [Fact]
    public void PresentOrRecreatedTarget_IsPhysicalResidue_RegardlessOfPastExecution()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var path = fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        var before = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        Assert.Equal(SessionCleanupState.PhysicalResidue, before.Sessions.Single().State);
        Assert.True(Delete(root, prepared.Plan).AllSucceeded);
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var recreated = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        Assert.Equal(TargetPresence.Present, Assert.Single(recreated.Targets).Presence);
        Assert.Equal(path, recreated.Targets.Single().AbsolutePath);
        Assert.Equal(SessionCleanupState.PhysicalResidue, recreated.Sessions.Single().State);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("unknown-version")]
    public void FailedMetadataRecheck_IsUnknown_NotAnEmptyReferenceSet(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        Assert.True(Delete(root, prepared.Plan).AllSucceeded);
        if (scenario == "missing") File.Delete(fixture.WorkspacePath);
        else fixture.Write(scenario);
        var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        Assert.Equal(SessionCleanupState.Unknown, post.Sessions.Single().State);
        Assert.Null(post.Sessions.Single().WorkspaceReferenceCount);
        Assert.True(post.HasUnknowns);
        Assert.Contains("未知", PostCleanupAuditText.Summary(post));
        Assert.All(post.Targets, x => Assert.Equal(TargetPresence.Absent, x.Presence));
    }

    [Fact]
    public void FreshMetadataChanges_AreReadInsteadOfUsingBeforeSnapshot()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        Assert.True(Delete(root, prepared.Plan).AllSucceeded);
        var doc = fixture.CreateDocument();
        doc["global"]!["archivedSessionIds"] = new JsonArray();
        doc["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"] = new JsonArray(WorkspaceFixture.PinnedId);
        File.WriteAllText(fixture.WorkspacePath, doc.ToJsonString());
        var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        Assert.Equal(0, post.Sessions.Single().WorkspaceReferenceCount);
        Assert.NotEqual(prepared.Audit.Snapshot.Workspace.SnapshotSha256, post.After!.Workspace.SnapshotSha256);
        Assert.Equal(SessionCleanupState.Unknown, post.Sessions.Single().State); // Attachment coverage remains unknown.
        Assert.DoesNotContain("元数据残留", PostCleanupAuditText.Summary(post));
        Assert.Contains("不能认定彻底清理", PostCleanupAuditText.Summary(post));
    }

    [Fact]
    public void NewLogGeneration_IsFoundEvenWhenAllOriginalTargetsAreAbsent()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        Assert.True(Delete(root, prepared.Plan).AllSucceeded);
        root.Factory.WriteSessionFile("synthetic-a", WorkspaceFixture.ArchivedId, "session.jsonl", FixtureFactory.Utf8(
            "{\"type\":\"session\",\"version\":0,\"id\":\"" + WorkspaceFixture.ArchivedId + "\",\"createdAt\":1767225600000}\n"));
        var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
        Assert.All(post.Targets, x => Assert.Equal(TargetPresence.Absent, x.Presence));
        Assert.Equal(1, post.Sessions.Single().ObservedLogCount);
        Assert.Equal(SessionCleanupState.PhysicalResidue, post.Sessions.Single().State);
    }

    [Fact]
    public void LockedMetadataAndReparseReplacement_AreNotReportedAsClean()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var log = fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        Assert.True(Delete(root, prepared.Plan).AllSucceeded);
        using (var locked = new FileStream(fixture.WorkspacePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
            Assert.Equal(SessionCleanupState.Unknown, post.Sessions.Single().State);
        }
        var target = new WorkspaceFixture(outside).WriteSession(WorkspaceFixture.ArchivedId);
        var bytes = File.ReadAllBytes(target);
        try
        {
            File.CreateSymbolicLink(log, target);
            var post = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit);
            Assert.Equal(TargetPresence.Unknown, post.Targets.Single().Presence);
            Assert.True(post.HasUnknowns);
            Assert.Equal(bytes, File.ReadAllBytes(target));
        }
        finally { if (File.Exists(log)) File.Delete(log); }
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void WrongBindingAndCancellation_CannotReuseAnAudit()
    {
        using var root = FixtureRoot.Create();
        using var other = FixtureRoot.Create();
        new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        var prepared = Prepare(root);
        var mismatched = new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan with { PlanId = Guid.NewGuid() }, prepared.Audit);
        Assert.Null(mismatched.After);
        Assert.All(mismatched.Sessions, x => Assert.Equal(SessionCleanupState.Unknown, x.State));
        Assert.Null(new PostCleanupAuditor().Audit(other.SessionsRoot, prepared.Plan, prepared.Audit).After);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PostCleanupAuditor().Audit(root.SessionsRoot, prepared.Plan, prepared.Audit, cts.Token));
    }
}
