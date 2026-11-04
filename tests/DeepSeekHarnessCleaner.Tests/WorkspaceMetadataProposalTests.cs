using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceMetadataProposalTests
{
    private static (CleanupPlan Plan, PreCleanupAudit Before, WorkspacePhysicalOutcome[] Outcomes, PostCleanupAudit After) Scenario(FixtureRoot root, bool skipPinned = false)
    {
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var pinnedPath = fixture.WriteSession(WorkspaceFixture.PinnedId);
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var plan = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), plan);
        if (skipPinned) File.AppendAllText(pinnedPath, "\n"); // Actual file change forces executor skip, not a fabricated execution result.
        var execution = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, null, null, root.SessionsRoot, true);
        Assert.False(execution.Aborted);
        var facts = execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath, x.Status switch
        {
            ItemStatus.Deleted => WorkspacePhysicalStatus.Deleted,
            ItemStatus.Skipped => WorkspacePhysicalStatus.Skipped,
            _ => WorkspacePhysicalStatus.Failed,
        })).ToArray();
        return (plan, before, facts, new PostCleanupAuditor().Audit(root.SessionsRoot, plan, before));
    }

    [Fact]
    public void ProposalUsesOnlySelectedDeletedSessions_AndPreservesSourceAndReferenceOrder()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var source = Scenario(root);
        var path = Path.Combine(root.StoragesRoot, "workspace.json");
        var bytes = File.ReadAllBytes(path);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, source.Before, source.Outcomes, source.After);
        Assert.Equal(WorkspaceSyncState.Proposed, proposal.State);
        Assert.Equal(4, proposal.ReferencesToRemove.Count); // Archive + pin + two workspace members; missing-log unselected ID retained.
        Assert.DoesNotContain(proposal.ReferencesToRemove, x => x.SessionId == WorkspaceFixture.MissingId);
        Assert.Equal(source.After.After!.Workspace.References.Where(x => x.SessionId != WorkspaceFixture.MissingId), proposal.ReferencesToRemove);
        Assert.Equal(source.After.After.Workspace.SnapshotSha256, proposal.SourceSha256);
        Assert.Equal(source.Plan.Targets, proposal.PhysicalTargets);
        Assert.Equal(source.Outcomes, proposal.DeletionOutcomes);
        Assert.Equal(source.After.Targets, proposal.AbsenceEvidence);
        Assert.Equal(source.After.ObservedUtc, proposal.RecheckedUtc);
        Assert.All(proposal.Sessions, x => Assert.True(x.EligibleForProposal));
        Assert.Single(WorkspaceMetadataPlan.ProductionWriteContractIds);
        Assert.Equal(bytes, File.ReadAllBytes(path)); // No member/title/order/timestamp JSON is rewritten by this task.
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        var repeat = new WorkspaceMetadataProposal().Build(source.Plan, source.Before, source.Outcomes, source.After);
        Assert.Equal(proposal.ReferencesToRemove, repeat.ReferencesToRemove);
        Assert.NotEqual(proposal.OperationId, repeat.OperationId);
    }

    [Fact]
    public void ActualPartialExecution_OnlySuccessfulAbsentSessionIsProposed()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root, skipPinned: true);
        Assert.Contains(source.Outcomes, x => x.Status == WorkspacePhysicalStatus.Skipped);
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, source.Before, source.Outcomes, source.After);
        Assert.Equal(WorkspaceSyncState.Proposed, proposal.State);
        Assert.Equal(2, proposal.ReferencesToRemove.Count);
        Assert.All(proposal.ReferencesToRemove, x => Assert.Equal(WorkspaceFixture.ArchivedId, x.SessionId));
        Assert.False(proposal.Sessions.Single(x => x.SessionId == WorkspaceFixture.PinnedId).EligibleForProposal);
        Assert.True(File.Exists(Path.Combine(root.SessionsRoot, "synthetic-a", WorkspaceFixture.PinnedId, "session.v4.jsonl")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing-outcome")]
    [InlineData("duplicate-outcome")]
    [InlineData("present")]
    [InlineData("unknown")]
    [InlineData("new-log")]
    [InlineData("partial-original-log-selection")]
    public void IncompleteOrContradictoryProof_CannotAuthorizeThatSessionsRemoval(string fault)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var path = source.Plan.Targets.Single(x => x.OwnerSessionId == WorkspaceFixture.ArchivedId).AbsolutePath;
        var outcomes = source.Outcomes;
        var after = source.After;
        var before = source.Before;
        if (fault == "failed") outcomes = outcomes.Select(x => x.AbsolutePath == path ? x with { Status = WorkspacePhysicalStatus.Failed } : x).ToArray();
        if (fault == "missing-outcome") outcomes = outcomes.Where(x => x.AbsolutePath != path).ToArray();
        if (fault == "duplicate-outcome") outcomes = outcomes.Append(outcomes.Single(x => x.AbsolutePath == path)).ToArray();
        if (fault is "present" or "unknown") after = after with
        {
            Targets = after.Targets.Select(x => x.AbsolutePath == path ? x with { Presence = fault == "present" ? TargetPresence.Present : TargetPresence.Unknown } : x).ToArray(),
        };
        if (fault == "new-log") after = after with
        {
            Sessions = after.Sessions.Select(x => x.SessionId == WorkspaceFixture.ArchivedId ? x with { ObservedLogCount = 1 } : x).ToArray(),
        };
        if (fault == "partial-original-log-selection") before = before with
        {
            Snapshot = before.Snapshot with
            {
                Sessions = before.Snapshot.Sessions.Select(x => x.SessionId == WorkspaceFixture.ArchivedId
                    ? x with { Items = x.Items.Select(i => i.Kind == SessionResidueKind.SessionLog ? i with { Scope = SessionResidueScope.NotInScope } : i).ToArray() } : x).ToArray(),
            },
        };
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, before, outcomes, after);
        Assert.DoesNotContain(proposal.ReferencesToRemove, x => x.SessionId == WorkspaceFixture.ArchivedId);
        Assert.False(proposal.Sessions.Single(x => x.SessionId == WorkspaceFixture.ArchivedId).EligibleForProposal);
        Assert.Contains(proposal.ReferencesToRemove, x => x.SessionId == WorkspaceFixture.PinnedId);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("unknown-index")]
    [InlineData("hash")]
    [InlineData("root")]
    [InlineData("pointer")]
    [InlineData("physical-scan")]
    public void UnboundOrUnknownSnapshot_BlocksWholeProposal(string fault)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var after = source.After;
        var snapshot = after.After!;
        if (fault == "plan") after = after with { PlanId = Guid.NewGuid() };
        if (fault == "unknown-index") snapshot = snapshot with { Workspace = snapshot.Workspace with { Status = WorkspaceReferenceStatus.Unknown } };
        if (fault == "hash") snapshot = snapshot with { Workspace = snapshot.Workspace with { SnapshotSha256 = null } };
        if (fault == "root") snapshot = snapshot with { DataHome = Path.Combine(root.Root, "other-home") };
        if (fault == "pointer") snapshot = snapshot with
        {
            Workspace = snapshot.Workspace with { References = [snapshot.Workspace.References[0] with { JsonPointer = "/global/unrelated/0" }] },
        };
        if (fault == "physical-scan") snapshot = snapshot with
        {
            Issues = snapshot.Issues.Append(new SessionResidueIssue("PATH_REPARSE", Path.Combine(root.SessionsRoot, "unknown"), null)).ToArray(),
        };
        after = after with { After = snapshot };
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, source.Before, source.Outcomes, after);
        Assert.Equal(WorkspaceSyncState.Blocked, proposal.State);
        Assert.Empty(proposal.ReferencesToRemove);
        Assert.NotEmpty(proposal.BlockReasons);
    }

    [Fact]
    public void MetadataOnlyOrphanWithoutOriginalLogTargets_IsNotEligible()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var before = source.Before with
        {
            Snapshot = source.Before.Snapshot with { Sessions = [new SessionResidueEntry(WorkspaceFixture.MissingId, [])] },
        };
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, before, source.Outcomes, source.After);
        Assert.Equal(WorkspaceSyncState.Blocked, proposal.State);
        Assert.Empty(proposal.ReferencesToRemove);
        Assert.False(Assert.Single(proposal.Sessions).EligibleForProposal);
        Assert.Equal("SYNC_FULL_LOG_SELECTION_REQUIRED", proposal.Sessions[0].Reason);
    }

    [Fact]
    public void FreshKnownNoReferences_IsNoOp_NotAnAppliedWrite()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var fixture = new WorkspaceFixture(root);
        var doc = fixture.CreateDocument();
        doc["global"]!["archivedSessionIds"] = new JsonArray();
        doc["global"]!["pinnedSessionIds"] = new JsonArray();
        doc["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"] = new JsonArray();
        File.WriteAllText(fixture.WorkspacePath, doc.ToJsonString()); // Synthetic current-state change, not a metadata writer test.
        var after = new PostCleanupAuditor().Audit(root.SessionsRoot, source.Plan, source.Before);
        var bytes = File.ReadAllBytes(fixture.WorkspacePath);
        var proposal = new WorkspaceMetadataProposal().Build(source.Plan, source.Before, source.Outcomes, after);
        Assert.Equal(WorkspaceSyncState.NoOp, proposal.State);
        Assert.Empty(proposal.ReferencesToRemove);
        Assert.All(proposal.Sessions, x => Assert.True(x.EligibleForProposal));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.WorkspacePath));
    }
}
