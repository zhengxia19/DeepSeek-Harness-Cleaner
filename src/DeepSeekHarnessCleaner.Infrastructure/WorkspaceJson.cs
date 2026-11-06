using System.Security.Cryptography;
using System.Text.Json;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Shared bounded JSON snapshot loading and duplicate-object-field checks; no schema policy.</summary>
internal static class WorkspaceJson
{
    internal const int MaxBytes = 8 * 1024 * 1024;

    internal static JsonDocument Load(string path, out string sha256, FileShare share = FileShare.Read)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        if (input.Length > MaxBytes) throw new InvalidDataException("WORKSPACE_TOO_LARGE");
        var bytes = BoundedRead.ReadAllUpTo(input, MaxBytes, out var exceeded);
        if (exceeded) throw new InvalidDataException("WORKSPACE_TOO_LARGE");
        sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // Stream parsing preserves the legacy reader's handling of a UTF-8 BOM.
        using var buffer = new MemoryStream(bytes, writable: false);
        return JsonDocument.Parse(buffer);
    }

    internal static bool UniqueObject(JsonElement element) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count();

    internal static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static string? DuplicateField(JsonElement element, string pointer = "")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var location = pointer + "/" + Escape(property.Name);
                if (!names.Add(property.Name)) return location;
                var child = DuplicateField(property.Value, location);
                if (child is not null) return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                var child = DuplicateField(item, pointer + "/" + i++);
                if (child is not null) return child;
            }
        }
        return null;
    }
}
