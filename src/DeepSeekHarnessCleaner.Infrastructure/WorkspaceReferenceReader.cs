using System.Globalization;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Strict read-only Workspace v2 single-file reference index. Never a metadata write gate.</summary>
public sealed class WorkspaceReferenceReader
{
    public WorkspaceReferenceIndex Read(string dataHome)
        => Read(dataHome, FileShare.Read);

    internal WorkspaceReferenceIndex Read(string dataHome, FileShare share)
    {
        var path = string.Empty;
        string? hash = null;
        WorkspaceReferenceIndex Failure(string reason, string? pointer = null, bool unsupported = false) => new(
            unsupported ? WorkspaceReferenceStatus.Unsupported : WorkspaceReferenceStatus.Unknown,
            path, hash, Array.Empty<WorkspaceReference>(), reason, pointer);
        try
        {
            path = Path.Combine(dataHome, "storages", "workspace.json");
            var guard = new PathGuard();
            if (!Path.IsPathFullyQualified(dataHome)) return Failure(PathGuard.RootInvalidErrorCode);
            var rootCheck = guard.Check(dataHome, dataHome);
            if (rootCheck.ErrorCode != PathGuard.PathEscapeErrorCode) return Failure(rootCheck.ErrorCode ?? PathGuard.RootInvalidErrorCode);
            var fullHome = Path.GetFullPath(dataHome);
            // Root ancestors may themselves be junctions; do not traverse such aliases for evidence.
            for (var parent = Directory.GetParent(fullHome); parent is not null; parent = parent.Parent)
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) return Failure(PathGuard.PathReparseErrorCode);
            var layout = Path.Combine(dataHome, "storages", "workspace");
            try
            {
                var attrs = File.GetAttributes(layout);
                var layoutCheck = guard.Check(dataHome, layout, allowDirectory: true);
                if (!layoutCheck.IsAllowed) return Failure(layoutCheck.ErrorCode ?? "WORKSPACE_LAYOUT_UNKNOWN");
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (File.Exists(path)) return Failure("WORKSPACE_LAYOUT_AMBIGUOUS");
                    path = layout;
                    return Failure("WORKSPACE_PER_RECORD_UNSUPPORTED", unsupported: true);
                }
                return Failure("WORKSPACE_LAYOUT_UNKNOWN");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            var check = guard.Check(dataHome, path);
            if (!check.IsAllowed) return Failure(check.ErrorCode ?? "WORKSPACE_MISSING");
            path = check.FullPath;
            using var document = WorkspaceJson.Load(path, out var snapshotHash, share);
            hash = snapshotHash;
            var root = document.RootElement;
            if (WorkspaceJson.DuplicateField(root) is { } duplicate) return Failure("WORKSPACE_DUPLICATE_FIELD", duplicate);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("unit", out var unit)
                || unit.ValueKind != JsonValueKind.Object || !unit.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !unit.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                return Failure("WORKSPACE_UNIT_INVALID", "/unit");
            if (name.GetString() != "workspace" || number != 2) return Failure("WORKSPACE_VERSION_UNSUPPORTED", "/unit", true);
            if (!root.TryGetProperty("global", out var global) || global.ValueKind != JsonValueKind.Object)
                return Failure("WORKSPACE_GLOBAL_INVALID", "/global");
            if (!global.TryGetProperty("initialized", out var initialized) || initialized.ValueKind != JsonValueKind.True)
                return Failure("WORKSPACE_NOT_INITIALIZED", "/global/initialized");
            if (global.TryGetProperty("pendingMutation", out _)) return Failure("WORKSPACE_TRANSACTION_UNKNOWN", "/global/pendingMutation");
            if (!Ids(global, "archivedSessionIds", true, out var archived) || !Ids(global, "pinnedSessionIds", true, out var pinned)
                || !Ids(global, "workspaceIds", false, out var workspaceIds)) return Failure("WORKSPACE_IDS_INVALID", "/global");
            if (archived.Intersect(pinned, StringComparer.Ordinal).Any()) return Failure("WORKSPACE_ARCHIVE_PIN_CONFLICT", "/global");
            if (global.TryGetProperty("defaultWorkspaceId", out var defaultId) && !Nonblank(defaultId))
                return Failure("WORKSPACE_DEFAULT_INVALID", "/global/defaultWorkspaceId");
            if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object
                || !tables.TryGetProperty("workspaces", out var workspaces) || workspaces.ValueKind != JsonValueKind.Object)
                return Failure("WORKSPACE_TABLE_INVALID", "/tables/workspaces");
            if (!workspaceIds.ToHashSet(StringComparer.Ordinal).SetEquals(workspaces.EnumerateObject().Select(x => x.Name)))
                return Failure("WORKSPACE_REGISTRY_MISMATCH", "/tables/workspaces");
            var references = new List<WorkspaceReference>();
            Add(archived, WorkspaceReferenceKind.Archived, null, "/global/archivedSessionIds");
            Add(pinned, WorkspaceReferenceKind.Pinned, null, "/global/pinnedSessionIds");
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in workspaceIds)
            {
                var pointer = "/tables/workspaces/" + WorkspaceJson.Escape(id);
                var record = workspaces.GetProperty(id);
                if (record.ValueKind != JsonValueKind.Object || !StringField(record, "path") || !StringField(record, "title")
                    || !DateField(record, "createdAt") || !DateField(record, "updatedAt") || !Ids(record, "sessionIds", false, out var ids))
                    return Failure("WORKSPACE_RECORD_INVALID", pointer);
                if (ids.Any(sessionId => !members.Add(sessionId))) return Failure("WORKSPACE_MEMBER_AMBIGUOUS", pointer + "/sessionIds");
                Add(ids, WorkspaceReferenceKind.WorkspaceMember, id, pointer + "/sessionIds");
            }
            return new WorkspaceReferenceIndex(WorkspaceReferenceStatus.Known, path, hash, references.AsReadOnly(), null, null);

            void Add(IReadOnlyList<string> ids, WorkspaceReferenceKind kind, string? workspaceId, string pointer)
            {
                for (var i = 0; i < ids.Count; i++) references.Add(new WorkspaceReference(ids[i], kind, workspaceId, pointer + "/" + i));
            }
        }
        catch (InvalidDataException) { return Failure("WORKSPACE_TOO_LARGE"); }
        catch (JsonException) { return Failure("WORKSPACE_JSON_INVALID"); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Failure(PathGuard.RootInvalidErrorCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Failure("WORKSPACE_READ_FAILED"); }
    }

    private static bool Nonblank(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
    private static bool StringField(JsonElement record, string key) => record.TryGetProperty(key, out var value) && Nonblank(value);
    private static bool DateField(JsonElement record, string key) => record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static bool Ids(JsonElement record, string key, bool optional, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        if (!record.TryGetProperty(key, out var array)) return optional;
        if (array.ValueKind != JsonValueKind.Array) return false;
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (!Nonblank(item) || !seen.Add(item.GetString()!)) return false;
            ordered.Add(item.GetString()!);
        }
        ids = ordered.AsReadOnly();
        return true;
    }
}
