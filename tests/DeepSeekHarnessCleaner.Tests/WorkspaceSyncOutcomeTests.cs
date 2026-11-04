using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceSyncOutcomeTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-10-20T08:00:00Z");
    private static WorkspaceSyncOutcome Sync(WorkspaceSyncState state, int? removed = 2, string? code = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), state, code, 2, removed, @"C:\private-synthetic-backup\SYNTHETIC_PRIVATE_ID", Observed);
    private static OutcomeReport Report(WorkspaceSyncOutcome? sync) => new(new OutcomeReportInput(false, null, null, null, 0, 0,
        [new("synthetic-deleted", CleanupCategory.SessionLog, null, OutcomeItemStatus.Deleted, null, 10),
         new("synthetic-failed", CleanupCategory.SessionLog, null, OutcomeItemStatus.Failed, "FILE_CHANGED", 0)])
    { WorkspaceSync = sync });

    [Fact]
    public void MetadataStateNeverRewritesPhysicalCountsOrBytes()
    {
        var report = Report(Sync(WorkspaceSyncState.Applied));
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(10, report.FreedBytes);
        Assert.True(report.PartialFailure);
        Assert.Contains("已验证移除 2", report.WorkspaceSyncSummary);
        Assert.Contains("不构成原子事务", report.WorkspaceSyncSummary);
        Assert.Contains("event=workspace_sync state=Applied proposed_refs=2 removed_refs=2", report.BuildLog());
    }

    [Fact]
    public void RecoveryDoesNotConvertUnknownRemovalToZero_AndLogsOmitPathsIdsAndFreeText()
    {
        const string secret = "synthetic-api-key-not-real";
        var outcome = Sync(WorkspaceSyncState.RecoveryRequired, null, secret);
        var report = Report(outcome);
        var log = report.BuildLog();
        Assert.Contains("removed_refs=unknown", log);
        Assert.Contains("code=OTHER_SYNC_ERROR", log);
        Assert.DoesNotContain(secret, log);
        Assert.DoesNotContain(outcome.BackupDirectory!, log);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ID", log);
        Assert.DoesNotContain(outcome.OperationId!.Value.ToString(), log);
        Assert.Contains("移除数量未知", report.WorkspaceSyncSummary);
        Assert.Equal(1, report.DeletedCount);
    }

    [Fact]
    public void OptOutAndLegacyReportRemainDistinct()
    {
        Assert.DoesNotContain("event=workspace_sync", Report(null).BuildLog());
        var report = Report(WorkspaceSyncOutcome.NotRequested(Guid.NewGuid()));
        Assert.Contains("state=NotRequested proposed_refs=0 removed_refs=0 backup_present=false", report.BuildLog());
        Assert.Contains("未请求，未写入元数据", report.WorkspaceSyncSummary);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void ContradictoryAppliedCountsAreNotPresentedAsVerified(int removed)
    {
        var report = Report(Sync(WorkspaceSyncState.Applied, removed));
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, report.WorkspaceSync!.State);
        Assert.Null(report.WorkspaceSync.RemovedReferenceCount);
        Assert.Contains("code=SYNC_REPORT_STATE_INVALID", report.BuildLog());
    }

    [Fact]
    public void WrongCleanupBindingCannotBeReportedApplied()
    {
        var plan = new CleanupPlan(Guid.NewGuid(), Observed, "synthetic-root", [], false);
        var result = new CleanupExecutionResult(false, null, []);
        var report = new ActionsPresenter().BuildReport(plan, null, result, "unused.zip", workspaceSync: Sync(WorkspaceSyncState.Applied));
        Assert.Equal(WorkspaceSyncState.Blocked, report.WorkspaceSync!.State);
        Assert.Equal("SYNC_REPORT_PLAN_MISMATCH", report.WorkspaceSync.ErrorCode);
    }
}
