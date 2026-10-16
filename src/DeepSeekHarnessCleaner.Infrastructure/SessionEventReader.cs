using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>单行事件的状态（design.md §33/§48：行号与状态必须可报告，未知事件不猜测语义）。</summary>
public enum EventLineStatus
{
    /// <summary>JSON 对象且 type 属于已验证的已知事件词汇。</summary>
    Valid,

    /// <summary>合法 JSON 对象但 type 未经验证；仅报告类型与行号，不解析正文。</summary>
    Unknown,

    /// <summary>JSON 语法不完整/损坏（TC-04 不完整 JSON）。</summary>
    MalformedJson,

    /// <summary>合法 JSON 但不是带字符串 type 的对象。</summary>
    NotObject,

    /// <summary>超过单行字节上限；只报行号，不保留正文（design.md §33）。</summary>
    TooLong,
}

/// <summary>一条物理行的读取结果；LineNumber 为 1 基，覆盖包括超限行在内的全部物理行。</summary>
public sealed record EventLine(int LineNumber, EventLineStatus Status, string? EventType = null);

/// <summary>
/// 整个事件文件的读取结果。仅 Success 表示全部行可当作正常事件流；
/// Partial/Corrupt 明确区分部分可读与整体损坏（TC-04、TC-20）。
/// </summary>
public sealed record SessionEventReadResult(
    ParseStatus Status,
    IReadOnlyList<EventLine> Lines,
    string? ErrorCode = null)
{
    public int UnknownCount => Lines.Count(l => l.Status == EventLineStatus.Unknown);

    public int MalformedCount => Lines.Count(l => l.Status is EventLineStatus.MalformedJson or EventLineStatus.NotObject);

    public bool IsReadComplete => Status == ParseStatus.Valid;
}

/// <summary>
/// T-12 安全 JSONL 事件流（design.md §33/§48）：消费解压字节逐行解析 JSON，
/// 限制解压总量与单行字节，返回行号/状态/未知事件摘要；全程只读，绝不修改日志。
/// 明文与 .zstd 多帧均支持；坏尾报 Partial，坏 JSON 行报 MalformedJson。
/// </summary>
public sealed class SessionEventReader
{
    public const string FileUnreadableErrorCode = "FILE_UNREADABLE";
    public const string EmptyFileErrorCode = "EVENTS_EMPTY";
    public const string AllLinesMalformedErrorCode = "EVENTS_ALL_MALFORMED";
    public const string ZstdTruncatedErrorCode = "ZSTD_TRUNCATED";
    public const string ZstdCorruptErrorCode = "ZSTD_CORRUPT";
    public const string TotalLimitErrorCode = "TOTAL_LIMIT";

    /// <summary>已知事件词汇由脱敏 fixture 断言绑定（design.md §48），不得凭字段名猜测扩充。</summary>
    public static readonly IReadOnlySet<string> KnownEventTypes =
        new HashSet<string>(StringComparer.Ordinal) { "header", "message", "attachment_ref" };

    /// <summary>
    /// 读取 <paramref name="fullPath"/> 的全部事件行。取消（<paramref name="cancellationToken"/>）
    /// 与超限路径均通过 using 释放文件句柄，不泄漏（验收：取消与超限不泄漏句柄）。
    /// </summary>
    public SessionEventReadResult Read(
        string fullPath,
        int maxLineBytes = CompressionLimits.MaxLineBytes,
        long maxTotalBytes = CompressionLimits.MaxSessionDecompressedBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxLineBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));

        byte[] content;
        bool partialFromPhysical = false;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fullPath.EndsWith(".zstd", StringComparison.Ordinal))
            {
                var decoded = ZstdFrameDecoder.Decode(stream, maxTotalBytes);
                switch (decoded.Status)
                {
                    case ZstdDecodeStatus.EmptyInput:
                        return new SessionEventReadResult(ParseStatus.Corrupt, [], EmptyFileErrorCode);
                    case ZstdDecodeStatus.TruncatedTail:
                        return new SessionEventReadResult(ParseStatus.Partial, [], ZstdTruncatedErrorCode);
                    case ZstdDecodeStatus.CorruptData:
                        return new SessionEventReadResult(ParseStatus.Corrupt, [], ZstdCorruptErrorCode);
                    case ZstdDecodeStatus.DecompressedLimitExceeded:
                        return new SessionEventReadResult(ParseStatus.Partial, [], TotalLimitErrorCode);
                }

                content = decoded.Content;
            }
            else
            {
                content = ReadPlainWithLimit(stream, maxTotalBytes, out partialFromPhysical);
                if (content.Length == 0)
                    return new SessionEventReadResult(ParseStatus.Corrupt, [], EmptyFileErrorCode);
            }
        }
        catch (IOException)
        {
            return new SessionEventReadResult(ParseStatus.Unreadable, [], FileUnreadableErrorCode);
        }
        catch (UnauthorizedAccessException)
        {
            return new SessionEventReadResult(ParseStatus.Unreadable, [], FileUnreadableErrorCode);
        }
        catch (SecurityException)
        {
            return new SessionEventReadResult(ParseStatus.Unreadable, [], FileUnreadableErrorCode);
        }

        var lines = ParseLines(content, maxLineBytes, cancellationToken);
        return Aggregate(lines, partialFromPhysical);
    }

    private static byte[] ReadPlainWithLimit(FileStream stream, long maxTotalBytes, out bool exceeded)
    {
        exceeded = false;
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxTotalBytes)
            {
                exceeded = true;
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static List<EventLine> ParseLines(byte[] content, int maxLineBytes, CancellationToken cancellationToken)
    {
        var lines = new List<EventLine>();
        var lineStart = 0;
        var lineNumber = 0;

        void AddLine(int start, int length)
        {
            lineNumber++;
            if (length > maxLineBytes)
            {
                lines.Add(new EventLine(lineNumber, EventLineStatus.TooLong));
                return;
            }

            var text = Encoding.UTF8.GetString(content, start, length);
            if (text.EndsWith('\r'))
                text = text[..^1];

            lines.Add(ParseLine(lineNumber, text));
        }

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != (byte)'\n')
                continue;

            AddLine(lineStart, i - lineStart);
            lineStart = i + 1;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (lineStart < content.Length)
            AddLine(lineStart, content.Length - lineStart);

        return lines;
    }

    private static EventLine ParseLine(int lineNumber, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return new EventLine(lineNumber, EventLineStatus.MalformedJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return new EventLine(lineNumber, EventLineStatus.NotObject);
            }

            var typeName = type.GetString();
            if (string.IsNullOrEmpty(typeName))
                return new EventLine(lineNumber, EventLineStatus.NotObject);

            return KnownEventTypes.Contains(typeName)
                ? new EventLine(lineNumber, EventLineStatus.Valid, typeName)
                : new EventLine(lineNumber, EventLineStatus.Unknown, typeName);
        }
    }

    private static SessionEventReadResult Aggregate(IReadOnlyList<EventLine> lines, bool totalExceeded)
    {
        if (lines.Count == 0)
            return new SessionEventReadResult(ParseStatus.Corrupt, lines, EmptyFileErrorCode);

        var malformed = lines.Count(l => l.Status is EventLineStatus.MalformedJson or EventLineStatus.NotObject);
        var tooLong = lines.Count(l => l.Status == EventLineStatus.TooLong);
        if (malformed == lines.Count)
            return new SessionEventReadResult(ParseStatus.Corrupt, lines, AllLinesMalformedErrorCode);

        if (totalExceeded || malformed > 0 || tooLong > 0)
            return new SessionEventReadResult(ParseStatus.Partial, lines);

        return new SessionEventReadResult(ParseStatus.Valid, lines);
    }
}
