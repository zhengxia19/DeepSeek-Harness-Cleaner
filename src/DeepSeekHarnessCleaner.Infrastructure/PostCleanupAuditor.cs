using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Fresh read-only post-audit. Executor result is deliberately not an input.</summary>
public sealed class PostCleanupAuditor
{
    public PostCleanupAudit Audit(string sessionRoot, CleanupPlan plan, PreCleanupAudit before, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(before);
        ct.ThrowIfCancellationRequested();
        var ids = before.Snapshot.Sessions.Select(x => x.SessionId).ToArray();
        PostCleanupAudit Unknown(string reason) => PostCleanupAudit.Unknown(plan.PlanId, ids, reason);
        if (!before.Matches(plan)) return Unknown("POST_AUDIT_PLAN_MISMATCH");
        try
        {
            if (!Path.IsPathFullyQualified(sessionRoot)
                || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionRoot)), "sessions", StringComparison.OrdinalIgnoreCase))
                return Unknown("POST_AUDIT_ROOT_INVALID");
            if (new CleanupPlanner().Build(sessionRoot, [], []).Plan?.RootFingerprint != plan.RootFingerprint)
                return Unknown("POST_AUDIT_ROOT_MISMATCH");
            var dataHome = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionRoot)))!;
            var guard = new PathGuard();
            if (guard.Check(dataHome, dataHome).ErrorCode != PathGuard.PathEscapeErrorCode) return Unknown("POST_AUDIT_ROOT_UNAVAILABLE");
            for (var parent = Directory.GetParent(dataHome); parent is not null; parent = parent.Parent)
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) return Unknown(PathGuard.PathReparseErrorCode);
            var after = new SessionResidueInventory().Scan(dataHome, ids, [], ct);
            var observations = new List<TargetPresenceEvidence>();
            foreach (var target in before.Targets)
            {
                ct.ThrowIfCancellationRequested();
                var check = guard.Check(dataHome, target.AbsolutePath);
                if (check.Status == PathCheckStatus.Missing)
                    observations.Add(new(target.AbsolutePath, target.Category, TargetPresence.Absent, null));
                else if (check.IsAllowed)
                {
                    try
                    {
                        var attributes = File.GetAttributes(target.AbsolutePath);
                        observations.Add((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0
                            ? new(target.AbsolutePath, target.Category, TargetPresence.Present, null)
                            : new(target.AbsolutePath, target.Category, TargetPresence.Unknown, "POST_TARGET_CHANGED_TYPE"));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    { observations.Add(new(target.AbsolutePath, target.Category, TargetPresence.Absent, null)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { observations.Add(new(target.AbsolutePath, target.Category, TargetPresence.Unknown, "POST_TARGET_UNREADABLE")); }
                }
                else observations.Add(new(target.AbsolutePath, target.Category, TargetPresence.Unknown, check.ErrorCode ?? "POST_TARGET_UNKNOWN"));
            }
            var issues = new List<SessionResidueIssue>();
            var results = new List<PostCleanupSessionResult>();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var selected in before.Snapshot.Sessions)
            {
                var linked = observations.Where(o => selected.Items.Any(i => i.Scope == SessionResidueScope.TargetFile
                    && string.Equals(i.EvidencePath, o.AbsolutePath, comparison))).ToArray();
                var current = after.Sessions.Single(x => x.SessionId == selected.SessionId);
                var logs = current.Items.Count(x => x.Kind == SessionResidueKind.SessionLog && x.Scope != SessionResidueScope.UnknownReference);
                int? metadata = after.Workspace.Status == WorkspaceReferenceStatus.Known
                    ? after.Workspace.References.Count(x => x.SessionId == selected.SessionId) : null;
                var unknown = after.HasUnknowns || linked.Any(x => x.Presence == TargetPresence.Unknown);
                var state = linked.Any(x => x.Presence == TargetPresence.Present) || logs > 0 ? SessionCleanupState.PhysicalResidue
                    : metadata > 0 ? SessionCleanupState.MetadataResidue
                    : unknown ? SessionCleanupState.Unknown : SessionCleanupState.NoObservedResidue;
                results.Add(new(selected.SessionId, state, linked, logs, metadata, unknown));
            }
            foreach (var observation in observations)
                if (!results.Any(x => x.Targets.Contains(observation))) issues.Add(new("POST_TARGET_ASSOCIATION_UNKNOWN", observation.AbsolutePath, null));
            ct.ThrowIfCancellationRequested();
            return new PostCleanupAudit(plan.PlanId, DateTimeOffset.UtcNow, results.AsReadOnly(), observations.AsReadOnly(), after, issues.AsReadOnly());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return Unknown("POST_AUDIT_READ_FAILED"); }
    }
}
