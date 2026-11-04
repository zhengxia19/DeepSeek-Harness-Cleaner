using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// Reads reviewed packaged code, without loading Harness or evaluating configuration.
/// A matching release is necessary but insufficient to authorize a metadata write.
/// </summary>
internal static class WorkspaceReleaseReader
{
    internal const string Release = "0.2.1-rc.2";
    private const int MaximumHeader = 32 * 1024 * 1024;
    private const int MaximumEntry = 8 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> ReviewedFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/main.js"] = "6f3d1ddd1106371452c2204be6dfb2e5460a8e8259cdd05cdccb5387b3f9a85a",
            ["dsh/node_modules/@deepseek-ai/dsh/package.json"] = "bea4288aa88eb54efe829686553100a75fdf09d11c5e3bf6b329badb9a0368dd",
            ["dsh/node_modules/@deepseek-ai/dsh-base/package.json"] = "238a9c488f0eb58933b931451445f3d5b087f9938c51a8626467425e1aff28b1",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/package.json"] = "62a904e28901801f7ed114867d84f813ec136133c43f21e0f575dd1db5f5289a",
            ["dsh/node_modules/@deepseek-ai/dsh-workspace/lib/index.js"] = "931ca0cb10c5102066c7723690a887f0e6e8e7ab0ffdee1a782686745b0b934f",
            ["dsh/node_modules/@deepseek-ai/dsh-storage-json/lib/index.js"] = "0a6b75d5569db3379edd93e863570170e1f4121b6c9fabd856f5b476cffe8df4",
            ["dsh/node_modules/@deepseek-ai/dsh-storage-domain/lib/index.js"] = "a54f7664d2f64fe04a08c7058ca5bff4fc69ed6a0d30c2bc11561b620698ef16",
            ["dsh/node_modules/@deepseek-ai/dsh-storage/lib/index.js"] = "1289574c24ecfa5b134e2750d578c00ae59654de7d00133e117c4cc34095f3a3",
            ["dsh/node_modules/@deepseek-ai/dsh-app-boot/lib/index.js"] = "5135179ac810e4f626f32ec3bdc3293dc7d2028e8aa92e5e0c006f1a33b78e76",
            ["dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js"] = "6ae7cb9d57516b73c8aa4317c514ed81d28f2680efcee56d8faf2e588bb28c48",
            ["dsh/node_modules/@deepseek-ai/dsh/lib/profile-boot.js"] = "30dc487766b018f20a2f54a646b2ea49974704d8b4eca2ec5a87cc0e88e79087",
            ["dsh/node_modules/@deepseek-ai/dsh/lib/profile-boot-C14i-8IN.js"] = "32d6345d7457e7c19c18bad07a85856e7ef7c560d52a1da042d68981ee127b4d",
            ["dsh/node_modules/@deepseek-ai/dsh-home-paths/lib/index.js"] = "9877a6117c5def69b61fae12f70a7a7400894a9dc4a9f7069ce4cc58aeb66e9d",
            ["dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml"] = "a2cc57868a4d016609801e28781262515483dde29b41c2742d9a3b27a6f52f77",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/cordis.patch.yml"] = "3e1f923309612ed8551141127409d662e78758e2d0d125218a3f70514417e472",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/presets/standard.patch.yml"] = "0d398d2a6872a73a106cb3bd19303ecccf1637e1fcad4d0bf9d8a72d2c2c68f5",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/presets/ptc.patch.yml"] = "9e355c947bf61c3d2fe9bf3214b386f83c0d99d37579ce22576d4b82cb6e1a2c",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/presets/minimal.patch.yml"] = "a2a9ba1c9c392a45466708f18bc0aefbf300577f9b544dc06049f73a8a5b3267",
            ["dsh/node_modules/@deepseek-ai/dsh-web-app/presets/cordis.patch.yml"] = "01b0fc94fd49136b332254a7ae5658e43ffe646f7248047dba65d499cfc74596",
        };

    internal static bool MatchesReviewedRelease(string archivePath)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!Matches(stream, ReviewedFiles)) return false;
            using var header = ReadHeader(stream, out var dataOffset);
            using var manifest = JsonDocument.Parse(ReadEntry(stream, header.RootElement, dataOffset, "package.json"));
            RequireUniqueKeys(manifest.RootElement);
            return manifest.RootElement.GetProperty("name").GetString() == "@deepseek-ai/dsh-desktop"
                && manifest.RootElement.GetProperty("version").GetString() == Release;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    // This tests only the byte reader and hashes. Callers cannot supply a production allowlist.
    internal static byte[]? ReadPacked(string archivePath, string entry)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var header = ReadHeader(stream, out var dataOffset);
        var node = header.RootElement;
        foreach (var component in entry.Split('/'))
        {
            if (!node.TryGetProperty("files", out var files) || !files.TryGetProperty(component, out node)) return null;
        }
        return ReadEntry(stream, header.RootElement, dataOffset, entry);
    }

    internal static bool Matches(Stream stream, IReadOnlyDictionary<string, string> expected)
    {
        if (expected.Count == 0) return false;
        using var header = ReadHeader(stream, out var dataOffset);
        foreach (var (entry, hash) in expected)
        {
            var bytes = ReadEntry(stream, header.RootElement, dataOffset, entry);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static JsonDocument ReadHeader(Stream stream, out long dataOffset)
    {
        stream.Position = 0;
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != 4) throw new InvalidDataException();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        if (size < 8 || size > MaximumHeader || size > stream.Length - 8) throw new InvalidDataException();
        var header = new byte[(int)size];
        stream.ReadExactly(header);
        var jsonSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (jsonSize == 0 || jsonSize > size - 8) throw new InvalidDataException();
        dataOffset = 8L + size;
        return JsonDocument.Parse(header.AsMemory(8, (int)jsonSize), new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static byte[] ReadEntry(Stream stream, JsonElement header, long dataOffset, string entry)
    {
        var node = header;
        foreach (var component in entry.Split('/'))
        {
            if (component.Length == 0 || component is "." or "..") throw new InvalidDataException();
            RequireUniqueKeys(node);
            var files = node.GetProperty("files");
            // Ambiguous duplicated keys must never select an arbitrary archive member.
            if (files.EnumerateObject().Count(p => p.NameEquals(component)) != 1) throw new InvalidDataException();
            node = files.GetProperty(component);
        }
        RequireUniqueKeys(node);
        if (node.TryGetProperty("link", out _) || node.TryGetProperty("unpacked", out _) || node.TryGetProperty("files", out _))
            throw new InvalidDataException();
        if (!long.TryParse(node.GetProperty("offset").GetString(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var offset)) throw new InvalidDataException();
        var size = node.GetProperty("size").GetInt32();
        if (offset < 0 || size < 0 || size > MaximumEntry || offset > stream.Length - dataOffset
            || size > stream.Length - dataOffset - offset) throw new InvalidDataException();
        stream.Position = checked(dataOffset + offset);
        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireUniqueKeys(JsonElement node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException();
    }
}
