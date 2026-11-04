namespace DeepSeekHarnessCleaner.Application;

/// <summary>Read-only preflight observations bound to the exact physical target list. Not an execution permit.</summary>
public sealed record PreCleanupAudit(Guid PlanId, string RootFingerprint, DateTimeOffset ObservedUtc,
    IReadOnlyList<CleanupTarget> Targets, SessionResidueSnapshot Snapshot)
{
    public bool Matches(CleanupPlan plan) => PlanId == plan.PlanId
        && string.Equals(RootFingerprint, plan.RootFingerprint, StringComparison.Ordinal)
        && Targets.SequenceEqual(plan.Targets);
}
