using System.Text.Json;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Read the verified workspace domain v2 archive set. Missing/invalid data remains unknown.</summary>
public sealed class WorkspaceArchiveReader
{
    public IReadOnlySet<string>? Read(string dataHome)
    {
        var path = Path.Combine(dataHome, "storages", "workspace.json");
        if (!new PathGuard().Check(dataHome, path).IsAllowed) return null;
        try
        {
            using var doc = WorkspaceJson.Load(path, out _);
            var root = doc.RootElement;
            if (!WorkspaceJson.UniqueObject(root) || !root.TryGetProperty("unit", out var unit) || !WorkspaceJson.UniqueObject(unit)
                || !unit.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "workspace"
                || !unit.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 2
                || !root.TryGetProperty("global", out var global) || !WorkspaceJson.UniqueObject(global)
                || !global.TryGetProperty("initialized", out var initialized) || initialized.ValueKind != JsonValueKind.True
                || !global.TryGetProperty("archivedSessionIds", out var archived) || archived.ValueKind != JsonValueKind.Array)
                return null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in archived.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!))
                    return null;
            }
            if (global.TryGetProperty("pinnedSessionIds", out var pinned))
            {
                if (pinned.ValueKind != JsonValueKind.Array) return null;
                foreach (var id in pinned.EnumerateArray())
                    if (id.ValueKind != JsonValueKind.String || ids.Contains(id.GetString()!)) return null;
            }
            return ids;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
