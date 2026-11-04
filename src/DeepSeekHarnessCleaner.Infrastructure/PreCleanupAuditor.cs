using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Independent read-only audit; never adds reference evidence to physical deletion targets.</summary>
public sealed class PreCleanupAuditor
{
    public PreCleanupAudit Audit(string sessionRoot, IReadOnlyList<string> selectedSessionIds,
        CleanupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sessionRoot)
            || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionRoot)), "sessions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AUDIT_SESSION_ROOT_INVALID", nameof(sessionRoot));
        if (RootGuard.TryRootFingerprint(sessionRoot) != plan.RootFingerprint) throw new InvalidOperationException("AUDIT_ROOT_MISMATCH");
        var targets = Array.AsReadOnly(plan.Targets.ToArray());
        var dataHome = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot)))!;
        var snapshot = new SessionResidueInventory().Scan(dataHome, selectedSessionIds, targets, ct);
        var result = new PreCleanupAudit(plan.PlanId, plan.RootFingerprint, DateTimeOffset.UtcNow, targets, snapshot);
        if (!result.Matches(plan)) throw new InvalidOperationException("AUDIT_PLAN_CHANGED");
        return result;
    }
}
