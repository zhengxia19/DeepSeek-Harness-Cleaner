using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Read-only session relation inventory; never expands or executes the supplied physical plan.</summary>
public sealed class SessionResidueInventory
{
    public SessionResidueSnapshot Scan(string dataHome, IReadOnlyList<string> selectedSessionIds,
        IReadOnlyList<CleanupTarget> targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedSessionIds);
        ArgumentNullException.ThrowIfNull(targets);
        ct.ThrowIfCancellationRequested();
        var issues = new List<SessionResidueIssue>();
        var unlinked = new List<SessionResidueItem>();
        var selected = selectedSessionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)
            .ToDictionary(x => x, _ => new List<SessionResidueItem>(), StringComparer.Ordinal);
        if (selected.Count != selectedSessionIds.Count) issues.Add(new("SELECTION_INVALID_OR_DUPLICATE", null, null));
        var workspace = new WorkspaceReferenceReader().Read(dataHome);
        SessionResidueSnapshot Result() => new(dataHome,
            selected.Select(x => new SessionResidueEntry(x.Key, x.Value.AsReadOnly())).ToArray(),
            unlinked.AsReadOnly(), issues.AsReadOnly(), workspace);
        var guard = new PathGuard();
        if (!Path.IsPathFullyQualified(dataHome) || guard.Check(dataHome, dataHome).ErrorCode != PathGuard.PathEscapeErrorCode)
        {
            issues.Add(new("ROOT_INVALID", dataHome, null));
            return Result();
        }
        for (var parent = Directory.GetParent(Path.GetFullPath(dataHome)); parent is not null; parent = parent.Parent)
        {
            try
            {
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) == 0) continue;
                issues.Add(new(PathGuard.PathReparseErrorCode, dataHome, null));
                return Result();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { issues.Add(new("ROOT_UNREADABLE", dataHome, null)); return Result(); }
        }
        var sessionRoot = Path.Combine(dataHome, "sessions");
        var storageRoot = Path.Combine(dataHome, "storages");
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var verifiedTargets = new List<CleanupTarget>();
        var observedTargetPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var check = guard.Check(dataHome, target.AbsolutePath);
            if (check.IsAllowed) verifiedTargets.Add(target with { AbsolutePath = check.FullPath });
            else issues.Add(new(check.ErrorCode ?? "PLAN_TARGET_MISSING", target.AbsolutePath, target.OwnerSessionId));
        }
        SessionResidueScope Scope(string path, CleanupCategory category, string? owner)
        {
            var match = verifiedTargets.Any(x => string.Equals(x.AbsolutePath, path, pathComparison) && x.Category == category
                && (x.OwnerSessionId == owner || (category == CleanupCategory.Attachment && x.OwnerSessionId is null)));
            if (match) observedTargetPaths.Add(path);
            return match ? SessionResidueScope.TargetFile : SessionResidueScope.NotInScope;
        }
        var discovery = new SessionLogDiscovery(guard).Discover(sessionRoot);
        if (!discovery.RootValid) issues.Add(new(discovery.RootErrorCode!, sessionRoot, null));
        foreach (var anomaly in discovery.Anomalies) issues.Add(new(anomaly.Reason, anomaly.FullPath, null));
        var summaries = new List<SessionSummary>();
        foreach (var candidate in discovery.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(candidate.FullPath);
            var logs = new List<PhysicalLog>();
            var valid = !candidate.IsAmbiguous;
            foreach (var file in candidate.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!guard.Check(sessionRoot, file.FullPath).IsAllowed)
                {
                    valid = false;
                    issues.Add(new("LOG_PATH_REJECTED", file.FullPath, null));
                    continue;
                }
                var header = new SessionHeaderReader().Read(file.FullPath, id);
                valid &= header.AllowsAutoCleanup && header.IdentityMatches == true
                    && file.Generation <= SessionHeaderReader.MaxSupportedFormatVersion
                    && (header.Header is not { IsNative: true } native || native.FormatVersion == file.Generation);
                try
                {
                    logs.Add(new PhysicalLog(file.FullPath, file.Generation, file.Encoding == SessionFileEncoding.Zstd,
                        file.Length, File.GetLastWriteTimeUtc(file.FullPath), null, header.Status));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { valid = false; issues.Add(new("LOG_UNREADABLE", file.FullPath, id)); }
            }
            summaries.Add(new SessionSummary(valid ? id : null, null, null, DateOrigin.Unknown, logs,
                valid ? ParseStatus.Valid : ParseStatus.Partial)
            { IsAmbiguous = !valid });
            if (!valid)
            {
                issues.Add(new("SESSION_IDENTITY_UNKNOWN", candidate.FullPath, null));
                if (selected.TryGetValue(id, out var items))
                    foreach (var file in candidate.Files) items.Add(new(SessionResidueKind.SessionLog, SessionResidueScope.UnknownReference,
                        file.FullPath, null, null, [], "SESSION_IDENTITY_UNKNOWN"));
            }
        }
        var duplicates = summaries.Where(x => x.SessionId is not null).GroupBy(x => x.SessionId!, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var summary in summaries.Where(x => x.SessionId is { } id && duplicates.Contains(id)))
            if (selected.TryGetValue(summary.SessionId!, out var items))
                foreach (var log in summary.Generations) items.Add(new(SessionResidueKind.SessionLog, SessionResidueScope.UnknownReference,
                    log.FullPath, null, null, [], "SESSION_IDENTITY_DUPLICATE"));
        summaries = summaries.Select(x => duplicates.Contains(x.SessionId ?? "") ? x with { SessionId = null, IsAmbiguous = true } : x).ToList();
        foreach (var id in duplicates) issues.Add(new("SESSION_IDENTITY_DUPLICATE", sessionRoot, id));
        foreach (var summary in summaries.Where(x => x.SessionId is not null))
        {
            var id = summary.SessionId!;
            if (!selected.TryGetValue(id, out var items)) continue;
            foreach (var log in summary.Generations) items.Add(new(SessionResidueKind.SessionLog,
                Scope(log.FullPath, CleanupCategory.SessionLog, id), log.FullPath, null, id, new[] { id }, null));
        }
        var knownIds = summaries.Select(x => x.SessionId).OfType<string>().ToArray();
        var caches = new SessionCacheIndex(guard).Build(storageRoot, knownIds);
        if (!caches.RootValid) issues.Add(new(caches.RootErrorCode!, storageRoot, null));
        foreach (var anomaly in caches.Anomalies) issues.Add(new(anomaly.Reason, anomaly.FullPath, null));
        foreach (var cache in caches.DedicatedCaches)
        {
            if (cache.Status == SessionCacheStatus.Matched && cache.SessionId is { } id)
            {
                if (selected.TryGetValue(id, out var items)) items.Add(new(SessionResidueKind.DedicatedCache,
                    Scope(cache.FullPath, CleanupCategory.DedicatedCache, id), cache.FullPath, null, id, new[] { id }, null));
            }
            else unlinked.Add(new(SessionResidueKind.DedicatedCache, SessionResidueScope.UnknownReference,
                cache.FullPath, null, null, [], cache.Reason));
        }
        foreach (var whole in caches.WholeCacheCandidates) unlinked.Add(new(SessionResidueKind.WholeCache,
            SessionResidueScope.NotInScope, whole.FullPath, null, null, [], "WHOLE_CACHE_REQUIRES_SEPARATE_SELECTION"));
        if (workspace.Status != WorkspaceReferenceStatus.Known) issues.Add(new(workspace.Reason ?? "WORKSPACE_UNKNOWN", workspace.EvidencePath, null));
        foreach (var reference in workspace.References)
            if (selected.TryGetValue(reference.SessionId, out var items)) items.Add(new(SessionResidueKind.WorkspaceReference,
                SessionResidueScope.NotInScope, workspace.EvidencePath, reference.JsonPointer, reference.WorkspaceId,
                new[] { reference.SessionId }, "WORKSPACE_METADATA_NOT_A_PHYSICAL_TARGET"));
        var attachments = new AttachmentInventoryService().Scan(dataHome, summaries, ct);
        foreach (var code in attachments.Issues) issues.Add(new(code, Path.Combine(dataHome, "attachments"), null));
        if (!attachments.GlobalOrphanConclusionAvailable) issues.Add(new("ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN", null, null));
        foreach (var id in attachments.UnresolvedSessionIds) issues.Add(new("ATTACHMENT_SESSION_COVERAGE_UNKNOWN", null, id));
        foreach (var attachment in attachments.Items)
        {
            var owners = attachment.ReferencingSessionIds;
            if (owners.Count == 0)
            {
                unlinked.Add(new(SessionResidueKind.Attachment, SessionResidueScope.UnknownReference,
                    attachment.Candidate.FullPath, null, attachment.AttachmentId, [], "ATTACHMENT_RELATION_UNKNOWN"));
                continue;
            }
            foreach (var id in owners)
                if (selected.TryGetValue(id, out var items)) items.Add(new(SessionResidueKind.Attachment,
                    Scope(attachment.Candidate.FullPath, CleanupCategory.Attachment, id), attachment.Candidate.FullPath, null,
                    attachment.AttachmentId, owners.ToArray(), owners.Count > 1 ? "SHARED_ATTACHMENT" : "REFERENCED_ATTACHMENT"));
        }
        foreach (var dangling in attachments.DanglingReferences)
            foreach (var id in dangling.SessionIds)
                if (selected.TryGetValue(id, out var items)) items.Add(new(SessionResidueKind.Attachment, SessionResidueScope.UnknownReference,
                    Path.Combine(dataHome, "attachments"), null, dangling.AttachmentId, dangling.SessionIds.ToArray(), "ATTACHMENT_FILE_MISSING"));
        foreach (var target in verifiedTargets.Where(x => !observedTargetPaths.Contains(x.AbsolutePath)))
            unlinked.Add(new(SessionResidueKind.UnlinkedPlanTarget, SessionResidueScope.UnknownReference,
                target.AbsolutePath, null, null, [], "PLAN_TARGET_NOT_LINKED_TO_SELECTED_SESSION"));
        ct.ThrowIfCancellationRequested();
        return Result();
    }
}
