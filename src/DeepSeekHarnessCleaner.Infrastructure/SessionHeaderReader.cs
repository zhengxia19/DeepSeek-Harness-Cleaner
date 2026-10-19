using System.Security;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>受限 header 行的语义字段。</summary>
public sealed record SessionHeaderInfo(
    string SessionId,
    int FormatVersion,
    DateTimeOffset CreatedAt,
    string? Cwd);

/// <summary>单文件头部读取结果；仅 Valid 且身份不冲突才进入可删白名单。</summary>
public sealed record SessionHeaderReadResult(
    ParseStatus Status,
    SessionHeaderInfo? Header,
    string? ErrorCode = null)
{
    /// <summary>与期望目录身份（会话目录名）的映射核对结果；未提供期望时为 null。</summary>
    public bool? IdentityMatches { get; init; }

    public bool HasValidHeader => Status == ParseStatus.Valid && Header is not null;

    /// <summary>design.md §2：无有效 header 或身份映射不成立的日志不允许自动清理。</summary>
    public bool AllowsAutoCleanup => HasValidHeader && IdentityMatches != false;
}

/// <summary>
/// T-09 受限日志头部读取（design.md §2/§3，M-02 对齐真实契约）：只读取每个物理文件的首条 header 行，
/// 验证 <c>type=="session"</c>、<c>id</c>（非空字符串）、<c>version</c>（int，0～<see cref="MaxSupportedFormatVersion"/>）、
/// <c>createdAt</c>（仅 epoch-millis JSON 数值；可选 cwd）；不解析其余事件行。
/// 旧假设格式（<c>type:"header"</c>/<c>sessionId</c>/<c>formatVersion</c>/ISO 字符串日期）不再接受，按 HEADER_MISSING 安全失败。
/// 明文按首行切分；.zstd 经 ZstdFrameDecoder 多帧解码。全程只读，源字节不变。
/// </summary>
public sealed class SessionHeaderReader
{
    public const int MaxSupportedFormatVersion = 4;

    public const string HeaderMissingErrorCode = "HEADER_MISSING";
    public const string HeaderMalformedErrorCode = "HEADER_MALFORMED";
    public const string HeaderIdMissingErrorCode = "HEADER_ID_MISSING";
    public const string HeaderVersionInvalidErrorCode = "HEADER_VERSION_INVALID";
    public const string HeaderVersionUnsupportedErrorCode = "HEADER_VERSION_UNSUPPORTED";
    public const string HeaderDateInvalidErrorCode = "HEADER_DATE_INVALID";
    public const string HeaderCwdInvalidErrorCode = "HEADER_CWD_INVALID";
    public const string HeaderLineTooLongErrorCode = "HEADER_LINE_TOO_LONG";
    public const string ZstdTruncatedErrorCode = "ZSTD_TRUNCATED";
    public const string ZstdCorruptErrorCode = "ZSTD_CORRUPT";
    public const string ZstdLimitErrorCode = "ZSTD_LIMIT";
    public const string FileUnreadableErrorCode = "FILE_UNREADABLE";

    /// <summary>
    /// 读取 <paramref name="fullPath"/> 的首条 header。<paramref name="expectedSessionId"/>
    /// 用于 design.md §29 的目录↔头标识映射核对（不匹配仍返回 Valid，但 IdentityMatches=false）。
    /// </summary>
    public SessionHeaderReadResult Read(
        string fullPath,
        string? expectedSessionId = null,
        int maxLineBytes = CompressionLimits.MaxLineBytes)
    {
        if (maxLineBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes));

        byte[] firstLine;
        try
        {
            firstLine = fullPath.EndsWith(".zstd", StringComparison.Ordinal)
                ? ReadFirstLineFromZstd(fullPath, maxLineBytes, out var zstdFailure)
                : ReadFirstLineFromPlain(fullPath, maxLineBytes, out zstdFailure);
            if (zstdFailure is not null)
                return zstdFailure;
        }
        catch (IOException ex)
        {
            return new SessionHeaderReadResult(ParseStatus.Unreadable, null, FileUnreadableErrorCode + ":" + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SessionHeaderReadResult(ParseStatus.Unreadable, null, FileUnreadableErrorCode + ":" + ex.GetType().Name);
        }
        catch (SecurityException ex)
        {
            return new SessionHeaderReadResult(ParseStatus.Unreadable, null, FileUnreadableErrorCode + ":" + ex.GetType().Name);
        }

        if (firstLine.Length == 0)
            return WithIdentity(new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderMissingErrorCode), expectedSessionId, null);

        var text = Encoding.UTF8.GetString(firstLine);
        var parsed = ParseHeaderLine(text);
        return WithIdentity(parsed, expectedSessionId, expectedSessionId is null ? null : string.Equals(parsed.Header?.SessionId, expectedSessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 同一会话目录内各代际 header 身份一致性（design.md §2：身份冲突 → Ambiguous）。
    /// 任一文件无有效 header，或有效 header 的 sessionId 不一致，均返回 false（不可删）。
    /// </summary>
    public static bool HeadersConsistent(IEnumerable<SessionHeaderReadResult> results)
    {
        string? id = null;
        foreach (var result in results)
        {
            if (!result.HasValidHeader)
                return false;
            if (id is null)
                id = result.Header!.SessionId;
            else if (!string.Equals(id, result.Header!.SessionId, StringComparison.Ordinal))
                return false;
        }

        return id is not null;
    }

    private static SessionHeaderReadResult WithIdentity(
        SessionHeaderReadResult result,
        string? expectedSessionId,
        bool? matches)
        => expectedSessionId is null ? result : result with { IdentityMatches = matches };

    private static byte[] ReadFirstLineFromPlain(string fullPath, int maxLineBytes, out SessionHeaderReadResult? failure)
    {
        failure = null;
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[maxLineBytes + 1];
        int read = ReadAtMost(stream, buffer, buffer.Length);
        if (read == 0)
            return Array.Empty<byte>();

        int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
        if (newline >= 0)
            return buffer[..newline];
        if (read > maxLineBytes)
        {
            failure = new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderLineTooLongErrorCode);
            return Array.Empty<byte>();
        }

        // 无换行的整个文件即首行。
        return buffer[..read];
    }

    private static byte[] ReadFirstLineFromZstd(string fullPath, int maxLineBytes, out SessionHeaderReadResult? failure)
    {
        failure = null;
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoded = ZstdFrameDecoder.Decode(stream);
        switch (decoded.Status)
        {
            case ZstdDecodeStatus.EmptyInput:
                return Array.Empty<byte>();
            case ZstdDecodeStatus.TruncatedTail:
                failure = new SessionHeaderReadResult(ParseStatus.Partial, null, ZstdTruncatedErrorCode);
                return Array.Empty<byte>();
            case ZstdDecodeStatus.CorruptData:
                failure = new SessionHeaderReadResult(ParseStatus.Corrupt, null, ZstdCorruptErrorCode);
                return Array.Empty<byte>();
            case ZstdDecodeStatus.DecompressedLimitExceeded:
                failure = new SessionHeaderReadResult(ParseStatus.Partial, null, ZstdLimitErrorCode);
                return Array.Empty<byte>();
        }

        var content = decoded.Content;
        int newline = Array.IndexOf(content, (byte)'\n');
        int firstLength = newline >= 0 ? newline : content.Length;
        if (firstLength > maxLineBytes)
        {
            failure = new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderLineTooLongErrorCode);
            return Array.Empty<byte>();
        }

        return content[..firstLength];
    }

    private static int ReadAtMost(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                break;
            total += read;
        }

        return total;
    }

    private static SessionHeaderReadResult ParseHeaderLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderMalformedErrorCode);
        }

        using (document)
        {
            var root = document.RootElement;
            // M-02 真实契约（DIAG-scan-coverage / DIAG-event-format 实测）：
            // 首行为 {"type":"session","id":"session-<uuid>","version":<int>,"createdAt":<epoch-millis 数值>,…}。
            // 旧假设格式（type:"header" 等）不再接受 → HEADER_MISSING（安全失败，不放宽门禁）。
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "session")
            {
                return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderMissingErrorCode);
            }

            if (!root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(id.GetString()))
            {
                return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderIdMissingErrorCode);
            }

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var formatVersion)
                || formatVersion < 0)
            {
                return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderVersionInvalidErrorCode);
            }

            if (formatVersion > MaxSupportedFormatVersion)
                return new SessionHeaderReadResult(ParseStatus.Unsupported, null, HeaderVersionUnsupportedErrorCode);

            // createdAt 仅接受 epoch-millis JSON 数值；字符串/缺失/非整数/超 DateTimeOffset 范围 → HEADER_DATE_INVALID。
            if (!root.TryGetProperty("createdAt", out var createdAt)
                || createdAt.ValueKind != JsonValueKind.Number
                || !createdAt.TryGetInt64(out var createdAtMillis))
            {
                return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderDateInvalidErrorCode);
            }

            DateTimeOffset created;
            try
            {
                created = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMillis);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderDateInvalidErrorCode);
            }

            // 其余字段（cwd/isSeeded/delegationDepth/agentPreset…）可选、不校验语义；cwd 仅约束 JSON 类型。
            string? cwd = null;
            if (root.TryGetProperty("cwd", out var cwdElement))
            {
                if (cwdElement.ValueKind != JsonValueKind.String)
                    return new SessionHeaderReadResult(ParseStatus.Corrupt, null, HeaderCwdInvalidErrorCode);
                cwd = cwdElement.GetString();
            }

            var header = new SessionHeaderInfo(id.GetString()!, formatVersion, created, cwd);
            return new SessionHeaderReadResult(ParseStatus.Valid, header);
        }
    }
}
