namespace DeepSeekHarnessCleaner.Application;

public enum OutcomeResidueCategory { SessionLog, DedicatedCache, Attachment, WorkspaceMetadata, OtherPhysical }

public sealed record OutcomeSessionRow(string SessionId, SessionCleanupState State,
    IReadOnlyList<OutcomeResidueCategory> ResidueCategories, int PresentTargets, int AbsentTargets, int UnknownTargets,
    int ObservedLogCount, int? WorkspaceReferenceCount, bool HasUnknowns);
