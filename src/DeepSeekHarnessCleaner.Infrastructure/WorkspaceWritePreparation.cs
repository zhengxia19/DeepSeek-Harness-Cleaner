using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Opaque, writer-instance-bound, single-use preparation. Public DTO state cannot construct this capability.</summary>
public sealed class WorkspaceWritePreparation
{
    internal WorkspaceWritePreparation(object owner, WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, WorkspaceFileIdentity identity, string storageBinding)
        => (Owner, Plan, Backup, Identity, StorageBinding) = (owner, plan, backup, identity, storageBinding);
    internal object Owner { get; }
    internal WorkspaceMetadataPlan Plan { get; }
    internal WorkspaceBackupEvidence Backup { get; }
    internal WorkspaceFileIdentity Identity { get; }
    internal string StorageBinding { get; }
    internal int Consumed;
    public Guid OperationId => Plan.OperationId;
}

public sealed record WorkspacePrepareResult(WorkspaceWritePreparation? Preparation, string? ErrorCode)
{
    public bool Success => Preparation is not null;
}

public sealed record WorkspaceWriteResult(WorkspaceSyncState State, string? ErrorCode, string? AppliedSha256,
    DateTimeOffset? AppliedUtc, string? JournalPath);
