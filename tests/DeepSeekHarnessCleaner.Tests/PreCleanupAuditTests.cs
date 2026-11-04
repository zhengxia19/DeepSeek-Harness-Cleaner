using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class PreCleanupAuditTests
{
    private static CleanupPlan Plan(FixtureRoot root)
    {
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        return new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
    }

    [Fact]
    public void AuditIsIndependent_ReadOnlyAndDoesNotExpandPhysicalTargets()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        root.Factory.WriteProjectionCache(WorkspaceFixture.ArchivedId);
        var attachment = root.Factory.WriteAttachment("unknown-retained");
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var hashes = Directory.GetFiles(root.Root, "*", SearchOption.AllDirectories).Append(sentinel)
            .ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(x)));
        var plan = Plan(root);
        var targets = plan.Targets.ToArray();
        var audit = new PreCleanupAuditor().Audit(root.SessionsRoot, [WorkspaceFixture.ArchivedId], plan);
        Assert.True(audit.Matches(plan));
        Assert.Equal(targets, audit.Targets);
        Assert.Equal(targets, plan.Targets);
        Assert.Single(plan.Targets);
        Assert.Contains(audit.Snapshot.Sessions.Single().Items, x => x.Kind == SessionResidueKind.DedicatedCache && x.Scope == SessionResidueScope.NotInScope);
        Assert.Contains(audit.Snapshot.Sessions.Single().Items, x => x.Kind == SessionResidueKind.WorkspaceReference && x.Scope == SessionResidueScope.NotInScope);
        Assert.True(audit.Snapshot.HasUnknowns);
        var text = PreCleanupAuditText.Render(audit);
        Assert.Contains("日志代际 session.v4.jsonl", text);
        Assert.Contains("专属缓存", text);
        Assert.Contains("Workspace 引用", text);
        Assert.Contains("#/", text);
        Assert.Contains("非本次清理范围", text);
        Assert.Contains("未知引用", text);
        Assert.Contains("不能据此认定会话已彻底清理", text);
        Assert.True(File.Exists(attachment));
        foreach (var file in hashes) Assert.Equal(file.Value, SHA256.HashData(File.ReadAllBytes(file.Key)));
    }

    [Fact]
    public void BindingRejectsOtherPlanRootOwnerAndTargetSet_ButNotBackupPreference()
    {
        using var root = FixtureRoot.Create();
        new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        var original = Plan(root);
        var mutable = original.Targets.ToList();
        var plan = original with { Targets = mutable };
        var audit = new PreCleanupAuditor().Audit(root.SessionsRoot, [WorkspaceFixture.ArchivedId], plan);
        Assert.True(audit.Matches(plan with { RequiresBackup = !plan.RequiresBackup }));
        Assert.False(audit.Matches(plan with { PlanId = Guid.NewGuid() }));
        Assert.False(audit.Matches(plan with { RootFingerprint = "different" }));
        Assert.False(audit.Matches(plan with { Targets = [plan.Targets[0] with { OwnerSessionId = "other" }] }));
        mutable.Clear();
        Assert.False(audit.Matches(plan));
        Assert.Single(audit.Targets);
    }

    [Fact]
    public void RootMismatchAndCancellationDoNotProduceReusableAudit()
    {
        using var root = FixtureRoot.Create();
        using var other = FixtureRoot.Create();
        new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        var plan = Plan(root);
        Assert.Throws<InvalidOperationException>(() => new PreCleanupAuditor().Audit(other.SessionsRoot, [WorkspaceFixture.ArchivedId], plan));
        Assert.Throws<ArgumentException>(() => new PreCleanupAuditor().Audit(root.DshHome, [WorkspaceFixture.ArchivedId], plan));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PreCleanupAuditor().Audit(root.SessionsRoot, [WorkspaceFixture.ArchivedId], plan, cts.Token));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unknown-version")]
    public void UnknownMetadataIsExplicitInConfirmation_DoesNotAddMetadataTargets(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        fixture.Write(scenario);
        var plan = Plan(root);
        var audit = new PreCleanupAuditor().Audit(root.SessionsRoot, [WorkspaceFixture.ArchivedId], plan);
        var text = PreCleanupAuditText.Render(audit);
        Assert.Contains("WORKSPACE_", text);
        Assert.Contains("无法确认项", text);
        Assert.DoesNotContain(plan.Targets, x => x.AbsolutePath == fixture.WorkspacePath);
        Assert.True(audit.Matches(plan));
    }
}
