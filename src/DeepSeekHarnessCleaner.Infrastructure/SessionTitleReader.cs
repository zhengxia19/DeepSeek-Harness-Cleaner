using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Read durable session/title log events only. Never writes session files.</summary>
public sealed class SessionTitleReader
{
    private const int MaxTitleBytes = 1024;
    private const int MaxLineBytes = 1024 * 1024;
    private const long MaxInspectBytes = 32L * 1024 * 1024;

    public string? Read(string path, bool compressed)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (compressed)
            {
                var decoded = ZstdFrameDecoder.Decode(input, MaxInspectBytes);
                if (decoded.Status != ZstdDecodeStatus.Success)
                    return null;
                using var memory = new MemoryStream(decoded.Content, writable: false);
                return Parse(memory);
            }
            if (input.Length > MaxInspectBytes) return null;
            return Parse(input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? Parse(Stream stream)
    {
        string? title = null;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024, leaveOpen: true);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "session/title"
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("title", out var value)
                    || value.ValueKind != JsonValueKind.String)
                    continue;
                var candidate = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && Encoding.UTF8.GetByteCount(candidate) <= MaxTitleBytes)
                    title = candidate;
            }
            catch (JsonException) { /* Skip malformed event; do not infer title from other events. */ }
        }
        return title;
    }
}
