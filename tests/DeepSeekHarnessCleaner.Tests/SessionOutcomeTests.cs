using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class SessionOutcomeTests
{
    private static PostCleanupAudit Audit(Guid planId, string id = "session-1", SessionCleanupState state = SessionCleanupState.MetadataResidue,
        int? metadata = 2, string code = "ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN")
    {
        var issue = new SessionResidueIssue(code, @"C:\synthetic-private\workspace.json", id);
        var after = new SessionResidueSnapshot(@"C:\synthetic-private", [new(id, [])], [], [issue],
            new WorkspaceReferenceIndex(WorkspaceReferenceStatus.Known, @"C:\synthetic-private\workspace.json", null, [], null, null));
        var target = new TargetPresenceEvidence(@"C:\synthetic-private\session.v4.jsonl", CleanupCategory.SessionLog, TargetPresence.Absent, null);
        return new PostCleanupAudit(planId, DateTimeOffset.Parse("2026-10-20T08:00:00Z"),
            [new(id, state, [target], 0, metadata, true)], [target], after, []);
    }

    private static OutcomeReport Report(PostCleanupAudit? audit) => new(new OutcomeReportInput(false, null, null, null, 0, 0,
        [new(@"C:\synthetic-private\session.v4.jsonl", CleanupCategory.SessionLog, "session-1", OutcomeItemStatus.Deleted, null, 10)])
    { SessionAudit = audit });

    [Fact]
    public void FileSuccessAndSessionResidueMetrics_AreIndependent()
    {
        var report = Report(Audit(Guid.NewGuid()));
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(10, report.FreedBytes);
        Assert.False(report.PartialFailure);
        Assert.Equal(1, report.MetadataResidueSessionCount);
        Assert.Equal(0, report.PhysicalResidueSessionCount);
        Assert.Equal(1, report.UnknownSessionCount);
        var session = Assert.Single(report.Sessions);
        Assert.Equal(SessionCleanupState.MetadataResidue, session.State);
        Assert.Equal(OutcomeResidueCategory.WorkspaceMetadata, Assert.Single(session.ResidueCategories));
        Assert.Equal(1, session.AbsentTargets);
        Assert.Equal(2, session.WorkspaceReferenceCount);
        var log = report.BuildLog();
        Assert.Contains("event=run_summary", log);
        Assert.Contains(" deleted=1 skipped=0 failed=0 freed_bytes=10", log);
        Assert.Contains("event=session_audit_summary sessions=1 physical_residue=0 metadata_residue=1 unknown=1", log);
        Assert.Contains("state=MetadataResidue residue=WorkspaceMetadata", log);
    }

    [Fact]
    public void UnknownReferenceCount_IsNotLoggedOrDisplayedAsKnownZero()
    {
        var report = Report(Audit(Guid.NewGuid(), state: SessionCleanupState.Unknown, metadata: null, code: "WORKSPACE_JSON_INVALID"));
        Assert.Null(report.Sessions.Single().WorkspaceReferenceCount);
        Assert.Equal(0, report.MetadataResidueSessionCount);
        Assert.Equal(1, report.UnknownSessionCount);
        var log = report.BuildLog();
        Assert.Contains("metadata_refs=unknown", log);
        Assert.Contains("code=WORKSPACE_JSON_INVALID", log);
        Assert.DoesNotContain("metadata_refs=0", log);
    }

    [Fact]
    public void NewAuditEvents_ExcludePathsPointersRawIdsAndCredentialLikeCodes()
    {
        const string privateId = "SYNTHETIC_PRIVATE_SESSION";
        const string secretCode = "synthetic-api-key-not-real"; // Shape passes old regex, but is not in audit code vocabulary.
        var report = Report(Audit(Guid.NewGuid(), privateId, code: secretCode));
        var log = report.BuildLog();
        Assert.DoesNotContain(privateId, log);
        Assert.DoesNotContain(secretCode, log);
        Assert.DoesNotContain("synthetic-private", log);
        Assert.DoesNotContain("workspace.json", log);
        Assert.DoesNotContain("session.v4.jsonl", log);
        Assert.DoesNotContain("/global/", log);
        Assert.Contains("event=session_audit session_index=1", log);
        Assert.Contains("code=OTHER_AUDIT_ISSUE session_index=1", log);
        Assert.Equal(privateId, report.Sessions.Single().SessionId); // UI keeps source identity, log uses ordinal only.
        Assert.Equal(secretCode, Assert.Single(report.AuditIssues).Code); // No silent loss in UI diagnostics.
    }

    [Fact]
    public void LegacyReportHasNoInventedSessionResultsOrNewLogEvents()
    {
        var report = Report(null);
        Assert.False(report.HasSessionAudit);
        Assert.Empty(report.Sessions);
        Assert.Empty(report.AuditIssues);
        Assert.DoesNotContain("session_audit", report.BuildLog());
        Assert.Equal(3, report.BuildLog().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void PresenterRejectsPostAuditFromAnotherPlan_WithoutChangingFileMetrics()
    {
        const string path = @"C:\synthetic\sessions\a\session.v4.jsonl";
        var plan = new CleanupPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, "synthetic-fingerprint",
            [new(path, CleanupCategory.SessionLog, "session-1", 10, DateTimeOffset.UtcNow, null)], false);
        var result = new CleanupExecutionResult(false, null, [new(path, ItemStatus.Deleted, null, 10)]);
        var report = new ActionsPresenter(() => []).BuildReport(plan, null, result, @"C:\synthetic\not-created.zip", Audit(Guid.NewGuid()));
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(SessionCleanupState.Unknown, report.Sessions.Single().State);
        Assert.Contains(report.AuditIssues, x => x.Code == "REPORT_POST_AUDIT_PLAN_MISMATCH");
        Assert.Contains("REPORT_POST_AUDIT_PLAN_MISMATCH", report.BuildLog());
    }

    [Fact]
    public void InvalidAuditValuesAreUnknown_AndResidueKindsComeOnlyFromPositiveEvidence()
    {
        var audit = Audit(Guid.NewGuid());
        var row = audit.Sessions.Single() with
        {
            State = (SessionCleanupState)99,
            WorkspaceReferenceCount = -1,
            ObservedLogCount = -1,
            Targets = [new("synthetic-path", CleanupCategory.DedicatedCache, TargetPresence.Present, null)],
        };
        var report = Report(audit with { Sessions = [row with { HasUnknowns = false }], After = audit.After! with { Issues = [] } });
        var session = Assert.Single(report.Sessions);
        Assert.Equal(SessionCleanupState.Unknown, session.State);
        Assert.Null(session.WorkspaceReferenceCount);
        Assert.Equal(0, session.ObservedLogCount);
        Assert.Equal(OutcomeResidueCategory.DedicatedCache, Assert.Single(session.ResidueCategories));
        Assert.True(report.HasAuditUnknowns);
        Assert.Contains("state=Unknown residue=DedicatedCache", report.BuildLog());
    }
}
