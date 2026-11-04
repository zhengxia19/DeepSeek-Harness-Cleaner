namespace DeepSeekHarnessCleaner.Application;

public enum TargetPresence { Present, Absent, Unknown }
public enum SessionCleanupState { PhysicalResidue, MetadataResidue, Unknown, NoObservedResidue }

public sealed record TargetPresenceEvidence(string AbsolutePath, CleanupCategory Category, TargetPresence Presence, string? Reason);
public sealed record PostCleanupSessionResult(string SessionId, SessionCleanupState State,
    IReadOnlyList<TargetPresenceEvidence> Targets, int ObservedLogCount, int? WorkspaceReferenceCount, bool HasUnknowns);

/// <summary>Fresh disk/metadata evidence, independent of executor success. NoObservedResidue is not a full cleanup guarantee.</summary>
public sealed record PostCleanupAudit(Guid PlanId, DateTimeOffset ObservedUtc,
    IReadOnlyList<PostCleanupSessionResult> Sessions, IReadOnlyList<TargetPresenceEvidence> Targets,
    SessionResidueSnapshot? After, IReadOnlyList<SessionResidueIssue> Issues)
{
    public bool HasUnknowns => After is null || After.HasUnknowns || Issues.Count > 0 || Sessions.Any(x => x.HasUnknowns);

    public static PostCleanupAudit Unknown(Guid planId, IEnumerable<string> sessionIds, string reason) => new(planId, DateTimeOffset.UtcNow,
        sessionIds.Distinct(StringComparer.Ordinal).Select(id => new PostCleanupSessionResult(id, SessionCleanupState.Unknown,
            Array.Empty<TargetPresenceEvidence>(), 0, null, true)).ToArray(), Array.Empty<TargetPresenceEvidence>(), null,
        new[] { new SessionResidueIssue(reason, null, null) });
}
