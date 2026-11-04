using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>Only Proposed/NoOp/Blocked are produced by T12. Later states require actual backup/writer evidence.</summary>
public enum WorkspaceSyncState { NotRequested, Proposed, BackupVerified, Prepared, Applying, Applied, NoOp, Blocked, RecoveryRequired }
public enum WorkspacePhysicalStatus { Deleted, Skipped, Failed }
public sealed record WorkspacePhysicalOutcome(string AbsolutePath, WorkspacePhysicalStatus Status);
public sealed record WorkspaceSessionDecision(string SessionId, bool EligibleForProposal, string Reason);

/// <summary>Pure removal proposal. No method on this DTO authorizes or performs a filesystem write.</summary>
public sealed record WorkspaceMetadataPlan(Guid OperationId, Guid CleanupPlanId, string RootFingerprint,
    string DataHome, string WorkspacePath, string? SourceSha256, DateTimeOffset ProposedUtc, DateTimeOffset RecheckedUtc,
    WorkspaceSyncState State, IReadOnlyList<WorkspaceReference> ReferencesToRemove,
    IReadOnlyList<WorkspaceSessionDecision> Sessions, IReadOnlyList<string> BlockReasons,
    IReadOnlyList<CleanupTarget> PhysicalTargets, IReadOnlyList<WorkspacePhysicalOutcome> DeletionOutcomes,
    IReadOnlyList<TargetPresenceEvidence> AbsenceEvidence)
{
    // Recognition alone is insufficient: installed fingerprints/configuration/root and all safety gates must match.
    public static IReadOnlyList<string> ProductionWriteContractIds { get; } = Array.AsReadOnly(
        new[] { "dsh-desktop@0.2.1-rc.2/workspace/v2/json-single" });
}
