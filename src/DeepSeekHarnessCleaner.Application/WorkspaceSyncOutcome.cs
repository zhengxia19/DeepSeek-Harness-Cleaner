namespace DeepSeekHarnessCleaner.Application;

/// <summary>Metadata outcome only. Physical outcomes and freed bytes remain independent; paths are UI-only.</summary>
public sealed record WorkspaceSyncOutcome(Guid CleanupPlanId, Guid? OperationId, WorkspaceSyncState State,
    string? ErrorCode, int ProposedReferenceCount, int? RemovedReferenceCount, string? BackupDirectory,
    DateTimeOffset ObservedUtc, string? AppliedSha256 = null)
{
    public static WorkspaceSyncOutcome NotRequested(Guid cleanupPlanId) => new(cleanupPlanId, null,
        WorkspaceSyncState.NotRequested, null, 0, 0, null, DateTimeOffset.UtcNow);
}
