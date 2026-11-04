using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

public enum SessionResidueKind { SessionLog, DedicatedCache, WholeCache, Attachment, WorkspaceReference, UnlinkedPlanTarget }
public enum SessionResidueScope { TargetFile, NotInScope, UnknownReference }

/// <summary>Read-only observed relation. Scope is classification against caller targets, never a new deletion target.</summary>
public sealed record SessionResidueItem(
    SessionResidueKind Kind, SessionResidueScope Scope, string EvidencePath,
    string? EvidencePointer, string? RelatedId, IReadOnlyList<string> RelatedSessionIds, string? Reason);

public sealed record SessionResidueIssue(string Code, string? EvidencePath, string? SessionId);
public sealed record SessionResidueEntry(string SessionId, IReadOnlyList<SessionResidueItem> Items);

/// <summary>
/// Facts only: retained NotInScope items are not deletion failures; HasUnknowns prevents a completeness claim.
/// No all-clean/success flag is provided. Snapshot observations require a fresh post-cleanup scan.
/// </summary>
public sealed record SessionResidueSnapshot(
    string DataHome, IReadOnlyList<SessionResidueEntry> Sessions,
    IReadOnlyList<SessionResidueItem> UnattributedItems, IReadOnlyList<SessionResidueIssue> Issues,
    WorkspaceReferenceIndex Workspace)
{
    public bool HasUnknowns => Workspace.Status != WorkspaceReferenceStatus.Known || Issues.Count > 0
        || UnattributedItems.Any(x => x.Scope == SessionResidueScope.UnknownReference)
        || Sessions.Any(x => x.Items.Any(i => i.Scope == SessionResidueScope.UnknownReference));
}
