using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Separate metadata attempts from irreversible physical deletion. Never rerun a physical executor.</summary>
public sealed class WorkspaceSyncCoordinator
{
    private readonly WorkspaceMetadataWriter _writer;
    private readonly HarnessProcessProbe _probe;
    private readonly WorkspaceRecoveryService _recovery;
    public WorkspaceSyncCoordinator(HarnessProcessProbe? probe = null)
    {
        _probe = probe ?? new HarnessProcessProbe();
        _writer = new WorkspaceMetadataWriter(_probe);
        _recovery = new WorkspaceRecoveryService(_probe);
    }
    internal WorkspaceSyncCoordinator(string fixtureHome, HarnessProcessProbe probe, Action<WorkspaceWritePoint>? fault = null)
    {
        _probe = probe;
        _writer = new WorkspaceMetadataWriter(fixtureHome, probe, fault);
        _recovery = new WorkspaceRecoveryService(fixtureHome, probe);
    }

    public WorkspaceSyncOption GetOption(CleanupPlan cleanup, PreCleanupAudit? before, string backupRoot)
    {
        int? count = null;
        WorkspaceSyncOption Disabled(string reason) => new(cleanup.PlanId, count, backupRoot, false, reason);
        try
        {
            if (before is null || !before.Matches(cleanup)) return Disabled("审计与删除计划不一致，请重新扫描。");
            var home = before.Snapshot.DataHome;
            if (new CleanupPlanner().Build(Path.Combine(home, "sessions"), [], []).Plan?.RootFingerprint != cleanup.RootFingerprint)
                return Disabled("数据根与当前计划不一致，请重新扫描。");
            var index = new WorkspaceReferenceReader().Read(home);
            if (index.Status != WorkspaceReferenceStatus.Known || index.SnapshotSha256 != before.Snapshot.Workspace.SnapshotSha256)
                return Disabled("Workspace 状态未知或已变化，请重新扫描。");
            var ids = before.Snapshot.Sessions.Where(x => x.Items.Any(i => i.Kind == SessionResidueKind.SessionLog)
                && x.Items.Where(i => i.Kind == SessionResidueKind.SessionLog).All(i => i.Scope == SessionResidueScope.TargetFile))
                .Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
            count = index.References.Count(x => ids.Contains(x.SessionId));
            if (!_writer.AllowsScope(home)) return Disabled("当前发行版本与存储后端尚未验证，禁止元数据同步。");
            if (!_probe.Probe().AllowsWrite) return Disabled("Harness 正在运行或状态未知。");
            _ = WorkspaceMetadataTransform.Load(index.EvidencePath);
            WorkspaceBackupService.ValidateDestination(home, backupRoot);
            if (!string.Equals(Path.GetPathRoot(backupRoot), Path.GetPathRoot(home), StringComparison.OrdinalIgnoreCase))
                return Disabled("强制备份目录须与数据根位于同一磁盘卷。");
            var storage = Path.GetDirectoryName(index.EvidencePath)!;
            if (new PathGuard().Check(home, Path.Combine(storage, ".dshcleaner-workspace-sync.lock")).Status != PathCheckStatus.Missing
                || Directory.GetFileSystemEntries(storage, ".dshcleaner-workspace-*.tmp").Length > 0)
                return Disabled("存在未结清的同步操作，请先复查恢复。");
            if (count == 0) return Disabled("所选完整会话没有可同步的已识别引用。");
            return new(cleanup.PlanId, count, backupRoot, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Text.Json.JsonException or System.Security.SecurityException or NotSupportedException)
        { return Disabled("元数据格式、路径或强制备份目录未通过验证，请复查后选择有效目录。"); }
    }

    public WorkspaceSyncAttempt Run(CleanupPlan cleanup, PreCleanupAudit before, CleanupExecutionResult execution,
        bool requested, string? backupRoot, CancellationToken ct = default)
    {
        WorkspaceSyncAttempt Result(WorkspaceSyncState state, string? code, WorkspaceMetadataPlan? plan = null,
            WorkspaceBackupEvidence? backup = null, PostCleanupAudit? audit = null, string? appliedSha = null) => new(
                new(cleanup.PlanId, plan?.OperationId, state, code, plan?.ReferencesToRemove.Count ?? 0,
                    state == WorkspaceSyncState.RecoveryRequired ? null : state == WorkspaceSyncState.Applied ? plan!.ReferencesToRemove.Count : 0,
                    backup is null ? null : Path.GetDirectoryName(backup.RawPath), DateTimeOffset.UtcNow, appliedSha), plan, backup, audit, backupRoot);
        if (!requested) return new(WorkspaceSyncOutcome.NotRequested(cleanup.PlanId), null, null, null, backupRoot);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!before.Matches(cleanup) || execution.Aborted) return Result(WorkspaceSyncState.Blocked, "SYNC_PHYSICAL_CONTEXT_INVALID");
            var sessionRoot = Path.Combine(before.Snapshot.DataHome, "sessions");
            var audit = new PostCleanupAuditor().Audit(sessionRoot, cleanup, before, ct);
            var facts = execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath, x.Status switch
            {
                ItemStatus.Deleted => WorkspacePhysicalStatus.Deleted,
                ItemStatus.Skipped => WorkspacePhysicalStatus.Skipped,
                _ => WorkspacePhysicalStatus.Failed,
            })).ToArray();
            var plan = new WorkspaceMetadataProposal().Build(cleanup, before, facts, audit);
            if (plan.State != WorkspaceSyncState.Proposed) return Result(plan.State, plan.BlockReasons.FirstOrDefault(), plan, audit: audit);
            if (!_writer.AllowsScope(plan.DataHome)) return Result(WorkspaceSyncState.Blocked, "SYNC_PRODUCTION_VERSION_NOT_ALLOWED", plan, audit: audit);
            if (backupRoot is null) return Result(WorkspaceSyncState.Blocked, "SYNC_BACKUP_ROOT_REQUIRED", plan, audit: audit);
            var backup = new WorkspaceBackupService(_probe).Create(plan, backupRoot, ct);
            if (!backup.Success) return Result(WorkspaceSyncState.Blocked, backup.ErrorCode, plan, audit: audit);
            var prepared = _writer.Prepare(plan, backup.Backup!, ct);
            if (!prepared.Success) return Result(WorkspaceSyncState.Blocked, prepared.ErrorCode, plan, backup.Backup, audit);
            var applied = _writer.Apply(prepared.Preparation!, ct);
            // Re-observe metadata and physical state after this separate operation, not the cached pre-write audit.
            PostCleanupAudit after;
            try { after = new PostCleanupAuditor().Audit(sessionRoot, cleanup, before, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { after = PostCleanupAudit.Unknown(cleanup.PlanId, before.Snapshot.Sessions.Select(x => x.SessionId), "SYNC_POST_AUDIT_FAILED"); }
            return Result(applied.State, applied.ErrorCode, plan, backup.Backup, after, applied.AppliedSha256);
        }
        catch (OperationCanceledException) { return Result(WorkspaceSyncState.Blocked, "SYNC_CANCELED"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return Result(WorkspaceSyncState.Blocked, "SYNC_COORDINATION_FAILED"); }
    }

    public WorkspaceSyncAttempt Retry(CleanupPlan cleanup, PreCleanupAudit before, CleanupExecutionResult execution,
        WorkspaceSyncAttempt previous, CancellationToken ct = default)
    {
        if (previous.Outcome.CleanupPlanId != cleanup.PlanId || previous.Outcome.State == WorkspaceSyncState.NotRequested)
            return new(new(cleanup.PlanId, null, WorkspaceSyncState.Blocked, "SYNC_RETRY_CONTEXT_INVALID", 0, 0, null, DateTimeOffset.UtcNow), null, null, null, null);
        if (previous.Outcome.State == WorkspaceSyncState.RecoveryRequired
            && (previous.Plan is null || previous.Backup is null || !_recovery.Inspect(previous.Plan, previous.Backup, ct).Resolved))
            return previous with { Outcome = previous.Outcome with { ErrorCode = "SYNC_RECOVERY_REQUIRED", ObservedUtc = DateTimeOffset.UtcNow } };
        // Run creates a fresh proposal/operation and snapshot after re-scanning. Old locks/candidates block Prepare;
        // resolving an interrupted operation is an explicit, separate recovery action.
        return Run(cleanup, before, execution, true, previous.BackupRoot, ct);
    }
}

public sealed record WorkspaceSyncAttempt(WorkspaceSyncOutcome Outcome, WorkspaceMetadataPlan? Plan,
    WorkspaceBackupEvidence? Backup, PostCleanupAudit? Audit, string? BackupRoot);
