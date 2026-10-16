using System.IO;
using System.Text;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.Tests.Fixtures;

/// <summary>
/// 合成夹具工厂：全部内容为合成数据（无真实会话正文/凭据），
/// 覆盖多版本日志、多代际、无效 JSON、日期、附件与安全目录。
/// </summary>
public sealed class FixtureFactory
{
    /// <summary>桌面清理白名单目录（docs/interfaces.md §3）。</summary>
    public static readonly string[] SafeDesktopDirectories =
    {
        "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "logs",
    };

    /// <summary>白名单外哨兵目录，必须保持不被清理计划触碰。</summary>
    public const string UnsafeDesktopDirectory = "Network";

    private readonly FixtureRoot _root;

    internal FixtureFactory(FixtureRoot root) => _root = root;

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public string WriteFile(string basePath, string relativePath, byte[] content, DateTimeOffset? modifiedUtc = null)
    {
        var full = Path.Combine(basePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        if (modifiedUtc is not null)
        {
            File.SetLastWriteTimeUtc(full, modifiedUtc.Value.UtcDateTime);
        }

        return full;
    }

    // ---- 合成 JSONL 行（模式由 T-09/T-12 固化，此处仅合成占位） ----

    public static string HeaderLine(string sessionId, int formatVersion, string createdAt) =>
        "{\"type\":\"header\",\"sessionId\":\"" + sessionId + "\",\"formatVersion\":" + formatVersion + ",\"createdAt\":\"" + createdAt + "\"}";

    public static string MessageLine(string role, string text, string ts) =>
        "{\"type\":\"message\",\"role\":\"" + role + "\",\"text\":\"" + text + "\",\"ts\":\"" + ts + "\"}";

    public static string AttachmentRefLine(string attachmentId) =>
        "{\"type\":\"attachment_ref\",\"attachmentId\":\"" + attachmentId + "\"}";

    public static string CorruptLine => "{\"type\":\"message\",\"text\":\"unclosed";

    public static string UnknownEventLine => "{\"type\":\"quantum_event\",\"payload\":{\"synthetic\":true}}";

    /// <summary>把每个明文字符串各自压缩为一个带内容 checksum 的 Zstd frame 并按序连接。</summary>
    public static byte[] ZstdFrames(params string[] plainFrames)
    {
        using var ms = new MemoryStream();
        foreach (var frame in plainFrames)
        {
            var bytes = ZstdFrameDecoder.CompressFrame(Utf8(frame));
            ms.Write(bytes, 0, bytes.Length);
        }

        return ms.ToArray();
    }

    // ---- 会话夹具 ----

    public string WriteSessionFile(string project, string sessionId, string fileName, byte[] content, DateTimeOffset? modifiedUtc = null) =>
        WriteFile(_root.SessionsRoot, Path.Combine(project, sessionId, fileName), content, modifiedUtc);

    /// <summary>有效 v0 明文会话：session.jsonl。</summary>
    public string WriteValidV0Session(string project = "proj-alpha", string sessionId = "synth-v0-001")
    {
        var content = string.Join(
            '\n',
            HeaderLine(sessionId, 0, "2026-01-05T09:00:00Z"),
            MessageLine("user", "synthetic v0 prompt", "2026-01-05T09:01:00Z")) + "\n";
        return WriteSessionFile(project, sessionId, "session.jsonl", Utf8(content), new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
    }

    /// <summary>有效 v4 单帧 zstd 会话：session.v4.jsonl.zstd。</summary>
    public string WriteValidV4Session(string project = "proj-alpha", string sessionId = "synth-v4-001")
    {
        var plain = string.Join(
            '\n',
            HeaderLine(sessionId, 4, "2026-02-10T10:00:00Z"),
            MessageLine("user", "synthetic v4 prompt", "2026-02-10T10:01:00Z"),
            MessageLine("assistant", "synthetic v4 reply", "2026-02-10T10:02:00Z")) + "\n";
        return WriteSessionFile(project, sessionId, "session.v4.jsonl.zstd", ZstdFrames(plain), new DateTimeOffset(2026, 2, 10, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>同一会话目录含 v0 明文 + v4 多帧 zstd（TC-03 代际聚合）。</summary>
    public void WriteMultiGenerationSession(string project = "proj-alpha", string sessionId = "synth-multi-001")
    {
        WriteSessionFile(project, sessionId, "session.jsonl",
            Utf8(HeaderLine(sessionId, 0, "2026-01-20T08:00:00Z") + "\n"),
            new DateTimeOffset(2026, 1, 20, 8, 0, 0, TimeSpan.Zero));
        var frame1 = HeaderLine(sessionId, 4, "2026-01-20T08:00:00Z") + "\n";
        var frame2 = MessageLine("user", "synthetic frame two", "2026-01-20T08:05:00Z") + "\n";
        WriteSessionFile(project, sessionId, "session.v4.jsonl.zstd",
            ZstdFrames(frame1, frame2),
            new DateTimeOffset(2026, 1, 21, 8, 0, 0, TimeSpan.Zero));
    }

    /// <summary>同代际 .jsonl 与 .jsonl.zstd 并存 → Ambiguous（design.md §2）。</summary>
    public void WriteAmbiguousSession(string project = "proj-beta", string sessionId = "synth-ambiguous-001")
    {
        var plain = HeaderLine(sessionId, 4, "2026-03-01T12:00:00Z") + "\n";
        WriteSessionFile(project, sessionId, "session.v4.jsonl", Utf8(plain));
        WriteSessionFile(project, sessionId, "session.v4.jsonl.zstd", ZstdFrames(plain));
    }

    /// <summary>含未闭合 JSON 行（TC-04 Corrupt）。</summary>
    public string WriteCorruptJsonSession(string project = "proj-beta", string sessionId = "synth-corrupt-001")
    {
        var content = HeaderLine(sessionId, 4, "2026-03-02T12:00:00Z") + "\n" + CorruptLine + "\n";
        return WriteSessionFile(project, sessionId, "session.v4.jsonl", Utf8(content));
    }

    /// <summary>含未知事件行（TC-06/TC-20）。</summary>
    public string WriteUnknownEventSession(string project = "proj-beta", string sessionId = "synth-unknown-001")
    {
        var content = HeaderLine(sessionId, 4, "2026-03-03T12:00:00Z") + "\n" + UnknownEventLine + "\n";
        return WriteSessionFile(project, sessionId, "session.v4.jsonl", Utf8(content));
    }

    /// <summary>多帧 zstd 但尾帧被截断（TC-04 Partial，不可当成功）。</summary>
    public string WriteTruncatedZstdSession(string project = "proj-gamma", string sessionId = "synth-truncated-001")
    {
        var bytes = ZstdFrames(
            HeaderLine(sessionId, 4, "2026-03-04T12:00:00Z") + "\n",
            MessageLine("user", "synthetic tail frame", "2026-03-04T12:01:00Z") + "\n");
        var truncated = new byte[bytes.Length - 8];
        Array.Copy(bytes, truncated, truncated.Length);
        return WriteSessionFile(project, sessionId, "session.v4.jsonl.zstd", truncated);
    }

    /// <summary>零字节会话文件。</summary>
    public string WriteEmptySession(string project = "proj-gamma", string sessionId = "synth-empty-001") =>
        WriteSessionFile(project, sessionId, "session.jsonl", Array.Empty<byte>());

    /// <summary>三个不同修改日期的会话，用于日期筛选夹具（TC-07）。</summary>
    public void WriteDatedSessions(string project = "proj-dates")
    {
        for (var day = 1; day <= 3; day++)
        {
            var sessionId = $"synth-dated-00{day}";
            var stamp = new DateTimeOffset(2026, 4, day, 6, 0, 0, TimeSpan.Zero);
            WriteSessionFile(project, sessionId, "session.v4.jsonl",
                Utf8(HeaderLine(sessionId, 4, stamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")) + "\n"),
                stamp);
        }
    }

    // ---- 附件与投影缓存 ----

    public string WriteAttachment(string attachmentId, byte[]? content = null, DateTimeOffset? modifiedUtc = null) =>
        WriteFile(_root.AttachmentsRoot, attachmentId + ".bin",
            content ?? Utf8("synthetic attachment payload for " + attachmentId),
            modifiedUtc ?? new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>会话日志中的附件引用行 + 对应附件文件。</summary>
    public void WriteSessionWithAttachmentReference(string project = "proj-attach", string sessionId = "synth-attach-001", string attachmentId = "att-synth-001")
    {
        var content = HeaderLine(sessionId, 4, "2026-05-02T12:00:00Z") + "\n" +
                      AttachmentRefLine(attachmentId) + "\n";
        WriteSessionFile(project, sessionId, "session.v4.jsonl", Utf8(content));
        WriteAttachment(attachmentId);
    }

    public void WriteProjectionCache(string sessionId, string? json = null)
    {
        WriteFile(_root.StoragesRoot, Path.Combine("session_projcache", "sessions", sessionId + ".json"),
            Utf8(json ?? "{\"schemaVersion\":1,\"sessionId\":\"" + sessionId + "\",\"synthetic\":true}"));
        WriteFile(_root.StoragesRoot, "session_projcache.json",
            Utf8("{\"schemaVersion\":1,\"synthetic\":true}"));
    }

    // ---- 桌面安全目录 ----

    /// <summary>创建 6 个白名单目录（logs 内含带日期日志）与白名单外哨兵目录。</summary>
    public void CreateDesktopSafeDirectories()
    {
        foreach (var dir in SafeDesktopDirectories)
        {
            Directory.CreateDirectory(Path.Combine(_root.DesktopHome, dir));
        }

        WriteFile(_root.DesktopHome, Path.Combine("logs", "main-2026-06-01.log"),
            Utf8("synthetic log line\n"), new DateTimeOffset(2026, 6, 1, 7, 0, 0, TimeSpan.Zero));
        WriteFile(_root.DesktopHome, Path.Combine("logs", "main-2026-06-02.log"),
            Utf8("synthetic log line two\n"), new DateTimeOffset(2026, 6, 2, 7, 0, 0, TimeSpan.Zero));
        WriteFile(_root.DesktopHome, Path.Combine(UnsafeDesktopDirectory, "sentinel.txt"),
            Utf8("must never be deleted\n"));
    }
}
