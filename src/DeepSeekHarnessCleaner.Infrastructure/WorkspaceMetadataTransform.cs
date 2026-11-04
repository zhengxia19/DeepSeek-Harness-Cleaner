using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Exact v2 known-field semantic transformation; never prune unrelated or unknown data.</summary>
internal static class WorkspaceMetadataTransform
{
    internal static JsonObject Load(string path)
    {
        using var document = WorkspaceJson.Load(path, out _, FileShare.Read | FileShare.Delete);
        if (WorkspaceJson.DuplicateField(document.RootElement) is not null) throw new InvalidDataException("SYNC_DUPLICATE_FIELD");
        var root = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        Fields(root, "unit", "global", "tables");
        Fields(root["unit"]!.AsObject(), "name", "version");
        var global = root["global"]!.AsObject();
        Fields(global, "initialized", "workspaceIds", "defaultWorkspaceId", "archivedSessionIds", "pinnedSessionIds");
        Fields(root["tables"]!.AsObject(), "workspaces");
        var workspaces = root["tables"]!["workspaces"]!.AsObject();
        foreach (var record in workspaces) Fields(record.Value!.AsObject(), "path", "title", "sessionIds", "createdAt", "updatedAt");
        if (global["defaultWorkspaceId"] is { } defaultId && !workspaces.ContainsKey(defaultId.GetValue<string>()))
            throw new InvalidDataException("SYNC_DEFAULT_NOT_REGISTERED");
        return root;
    }

    internal static byte[] Build(JsonObject original, IReadOnlySet<string> ids, DateTimeOffset appliedUtc)
    {
        var candidate = original.DeepClone().AsObject();
        Filter(candidate["global"]!.AsObject(), "archivedSessionIds", ids);
        Filter(candidate["global"]!.AsObject(), "pinnedSessionIds", ids);
        var timestamp = appliedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        foreach (var entry in candidate["tables"]!["workspaces"]!.AsObject())
        {
            var record = entry.Value!.AsObject();
            if (Filter(record, "sessionIds", ids)) record["updatedAt"] = timestamp;
        }
        Validate(original, candidate, ids, timestamp);
        return JsonSerializer.SerializeToUtf8Bytes(candidate, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static void Validate(JsonObject original, JsonObject candidate, IReadOnlySet<string> ids, string timestamp)
    {
        Require(JsonNode.DeepEquals(original["unit"], candidate["unit"]));
        var oldGlobal = original["global"]!.AsObject();
        var newGlobal = candidate["global"]!.AsObject();
        Require(oldGlobal.Select(x => x.Key).SequenceEqual(newGlobal.Select(x => x.Key)));
        foreach (var field in oldGlobal)
            if (field.Key is "archivedSessionIds" or "pinnedSessionIds") VerifyArray(field.Value!, newGlobal[field.Key]!, ids);
            else Require(JsonNode.DeepEquals(field.Value, newGlobal[field.Key]));
        var oldRecords = original["tables"]!["workspaces"]!.AsObject();
        var newRecords = candidate["tables"]!["workspaces"]!.AsObject();
        Require(oldRecords.Select(x => x.Key).SequenceEqual(newRecords.Select(x => x.Key)));
        foreach (var entry in oldRecords)
        {
            var before = entry.Value!.AsObject();
            var after = newRecords[entry.Key]!.AsObject();
            Require(before.Select(x => x.Key).SequenceEqual(after.Select(x => x.Key)));
            var changed = before["sessionIds"]!.AsArray().Any(x => ids.Contains(x!.GetValue<string>()));
            foreach (var field in before)
                if (field.Key == "sessionIds") VerifyArray(field.Value!, after[field.Key]!, ids);
                else if (field.Key == "updatedAt" && changed) Require(after[field.Key]!.GetValue<string>() == timestamp);
                else Require(JsonNode.DeepEquals(field.Value, after[field.Key]));
        }
    }

    private static bool Filter(JsonObject record, string key, IReadOnlySet<string> ids)
    {
        if (record[key] is not JsonArray array) return false; // Optional absent array stays absent.
        var changed = false;
        for (var i = array.Count - 1; i >= 0; i--)
            if (ids.Contains(array[i]!.GetValue<string>())) { array.RemoveAt(i); changed = true; }
        return changed;
    }

    private static void VerifyArray(JsonNode before, JsonNode after, IReadOnlySet<string> ids) => Require(
        before.AsArray().Select(x => x!.GetValue<string>()).Where(x => !ids.Contains(x))
            .SequenceEqual(after.AsArray().Select(x => x!.GetValue<string>())));
    private static void Fields(JsonObject value, params string[] allowed) => Require(value.All(x => allowed.Contains(x.Key, StringComparer.Ordinal)));
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("SYNC_SEMANTIC_INVARIANT_FAILED"); }
}
