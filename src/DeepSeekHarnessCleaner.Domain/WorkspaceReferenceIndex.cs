namespace DeepSeekHarnessCleaner.Domain;

public enum WorkspaceReferenceStatus { Known, Unknown, Unsupported }
public enum WorkspaceReferenceKind { Archived, Pinned, WorkspaceMember }

/// <summary>A proven reference in a specific JSON snapshot; JsonPointer follows RFC 6901 escaping.</summary>
public sealed record WorkspaceReference(string SessionId, WorkspaceReferenceKind Kind, string? WorkspaceId, string JsonPointer);

/// <summary>
/// Read-only reference evidence. Empty References means no references only when Status is Known.
/// SnapshotSha256 identifies the bytes read, not the latest disk state or authorization to write.
/// </summary>
public sealed record WorkspaceReferenceIndex(
    WorkspaceReferenceStatus Status,
    string EvidencePath,
    string? SnapshotSha256,
    IReadOnlyList<WorkspaceReference> References,
    string? Reason,
    string? ErrorPointer);
