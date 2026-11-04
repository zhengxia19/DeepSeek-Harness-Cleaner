using System.Globalization;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>Build only an in-memory proposal from explicit selection, physical outcomes and fresh post-audit.</summary>
public sealed class WorkspaceMetadataProposal
{
    public WorkspaceMetadataPlan Build(CleanupPlan cleanup, PreCleanupAudit before,
        IReadOnlyList<WorkspacePhysicalOutcome> outcomes, PostCleanupAudit after)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(after);
        var current = after.After?.Workspace;
        var dataHome = after.After?.DataHome ?? before.Snapshot.DataHome;
        var path = current?.EvidencePath ?? before.Snapshot.Workspace.EvidencePath;
        var decisions = new List<WorkspaceSessionDecision>();
        WorkspaceMetadataPlan Plan(WorkspaceSyncState state, IEnumerable<WorkspaceReference> references, params string[] reasons) => new(
            Guid.NewGuid(), cleanup.PlanId, cleanup.RootFingerprint, dataHome, path, current?.SnapshotSha256, DateTimeOffset.UtcNow, after.ObservedUtc,
            state, Array.AsReadOnly(references.ToArray()), decisions.AsReadOnly(), Array.AsReadOnly(reasons),
            Array.AsReadOnly(cleanup.Targets.ToArray()), Array.AsReadOnly(outcomes.ToArray()), Array.AsReadOnly(after.Targets.ToArray()));
        if (!before.Matches(cleanup) || after.PlanId != cleanup.PlanId) return Plan(WorkspaceSyncState.Blocked, [], "SYNC_PLAN_BINDING_MISMATCH");
        if (after.After is null || current?.Status != WorkspaceReferenceStatus.Known)
            return Plan(WorkspaceSyncState.Blocked, [], "SYNC_REFERENCE_SNAPSHOT_UNKNOWN");
        if (current.SnapshotSha256 is not { Length: 64 } sha || sha.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return Plan(WorkspaceSyncState.Blocked, [], "SYNC_SNAPSHOT_HASH_MISSING");
        try
        {
            if (!Same(dataHome, before.Snapshot.DataHome) || !Same(path, Path.Combine(dataHome, "storages", "workspace.json")))
                return Plan(WorkspaceSyncState.Blocked, [], "SYNC_ROOT_MISMATCH");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return Plan(WorkspaceSyncState.Blocked, [], "SYNC_ROOT_MISMATCH"); }
        if (current.References.Any(x => !ValidReference(x))) return Plan(WorkspaceSyncState.Blocked, [], "SYNC_REFERENCE_EVIDENCE_INVALID");
        if (after.Issues.Count > 0) return Plan(WorkspaceSyncState.Blocked, [], "SYNC_POST_AUDIT_INCOMPLETE");
        var sessionRoot = Path.Combine(dataHome, "sessions");
        if (after.After.Issues.Any(x => IsPhysicalIssue(x, sessionRoot)))
            return Plan(WorkspaceSyncState.Blocked, [], "SYNC_PHYSICAL_SCAN_INCOMPLETE");
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in before.Snapshot.Sessions)
        {
            var logs = session.Items.Where(x => x.Kind == SessionResidueKind.SessionLog).ToArray();
            if (logs.Length == 0 || logs.Any(x => x.Scope != SessionResidueScope.TargetFile))
            { decisions.Add(new(session.SessionId, false, "SYNC_FULL_LOG_SELECTION_REQUIRED")); continue; }
            var targets = cleanup.Targets.Where(t => session.Items.Any(i => i.Scope == SessionResidueScope.TargetFile && Same(i.EvidencePath, t.AbsolutePath))).ToArray();
            if (targets.Length == 0 || targets.Any(t => outcomes.Count(o => Same(o.AbsolutePath, t.AbsolutePath)) != 1
                || outcomes.Single(o => Same(o.AbsolutePath, t.AbsolutePath)).Status != WorkspacePhysicalStatus.Deleted))
            { decisions.Add(new(session.SessionId, false, "SYNC_PHYSICAL_DELETE_NOT_SUCCEEDED")); continue; }
            if (targets.Any(t => after.Targets.Count(o => Same(o.AbsolutePath, t.AbsolutePath) && o.Category == t.Category) != 1
                || after.Targets.Single(o => Same(o.AbsolutePath, t.AbsolutePath) && o.Category == t.Category).Presence != TargetPresence.Absent))
            { decisions.Add(new(session.SessionId, false, "SYNC_TARGET_ABSENCE_NOT_CONFIRMED")); continue; }
            var observed = after.Sessions.Where(x => x.SessionId == session.SessionId).ToArray();
            if (observed.Length != 1 || observed[0].ObservedLogCount != 0)
            { decisions.Add(new(session.SessionId, false, "SYNC_REMAINING_LOGS_OR_UNKNOWN")); continue; }
            eligible.Add(session.SessionId);
            decisions.Add(new(session.SessionId, true, current.References.Any(x => x.SessionId == session.SessionId) ? "SYNC_REMOVAL_PROPOSED" : "SYNC_NO_REFERENCES"));
        }
        // Preserve reference evidence order; writer must filter by eligible ID, never apply cached array indices blindly.
        var removals = current.References.Where(x => eligible.Contains(x.SessionId)).ToArray();
        return Plan(removals.Length > 0 ? WorkspaceSyncState.Proposed : decisions.Any(x => !x.EligibleForProposal) ? WorkspaceSyncState.Blocked : WorkspaceSyncState.NoOp,
            removals, removals.Length == 0 && decisions.Any(x => !x.EligibleForProposal) ? ["SYNC_NO_ELIGIBLE_REMOVALS"] : []);
    }

    private static bool Same(string left, string right) => Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsPhysicalIssue(SessionResidueIssue issue, string sessionRoot)
    {
        if (issue.Code.StartsWith("SESSION_IDENTITY_", StringComparison.Ordinal) || issue.Code.StartsWith("LOG_", StringComparison.Ordinal)) return true;
        if (issue.EvidencePath is null) return false;
        try
        {
            var relative = Path.GetRelativePath(sessionRoot, issue.EvidencePath);
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return true; }
    }

    private static bool ValidReference(WorkspaceReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.SessionId)) return false;
        var prefix = reference.Kind switch
        {
            WorkspaceReferenceKind.Archived when reference.WorkspaceId is null => "/global/archivedSessionIds/",
            WorkspaceReferenceKind.Pinned when reference.WorkspaceId is null => "/global/pinnedSessionIds/",
            WorkspaceReferenceKind.WorkspaceMember when !string.IsNullOrWhiteSpace(reference.WorkspaceId) =>
                "/tables/workspaces/" + reference.WorkspaceId.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) + "/sessionIds/",
            _ => "",
        };
        if (prefix.Length == 0 || !reference.JsonPointer.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var tail = reference.JsonPointer[prefix.Length..];
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && position >= 0 && tail == position.ToString(CultureInfo.InvariantCulture);
    }
}
