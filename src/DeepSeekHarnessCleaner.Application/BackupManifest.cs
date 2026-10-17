using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// manifest 单条目（interfaces.md §ZIP 导出契约 schemaVersion=1）：
/// zipPath 即 ZIP 条目名（sessions/&lt;relative&gt; 或 attachments/&lt;relative&gt;），
/// relativePath 为相对数据根、'/' 分隔的规范化路径；Sha256 为小写 64 位十六进制。
/// </summary>
public sealed record ManifestEntry(
    string ZipPath,
    string RelativePath,
    long Length,
    string Sha256,
    string? SessionId,
    IReadOnlyList<string> Warnings);

/// <summary>预期清单（schemaVersion=1）。Root 为数据根绝对路径。</summary>
public sealed record BackupManifest(
    int SchemaVersion,
    string Root,
    IReadOnlyList<ManifestEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>构建/校验问题（错误码 + 涉及路径 + 说明）。非空错误即拒绝。</summary>
public sealed record ManifestIssue(string Code, string Path, string Message);

/// <summary>构建结果：错误非空时 Manifest 为 null（不产出部分清单）。</summary>
public sealed record ManifestBuildResult(BackupManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    public bool Success => Manifest is not null;
}

/// <summary>校验结果：Success 当且仅当无任何问题。</summary>
public sealed record ManifestVerifyResult(IReadOnlyList<ManifestIssue> Issues)
{
    public bool Success => Issues.Count == 0;
}

/// <summary>manifest JSON 解析结果。</summary>
public sealed record ManifestLoadResult(BackupManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    public bool Success => Manifest is not null;
}

/// <summary>
/// ZIP manifest 构建与清单签名（T-21，interfaces.md §ZIP 导出契约）。
/// 从选中会话的全部已识别代际物理文件构建预期清单：逐文件流式 SHA-256 与长度；
/// 条目必须位于数据根下 sessions/ 或 attachments/ 两类目录内；
/// 拒绝路径遍历（..）、跨根、重复规范化条目与未知类别文件。
/// 校验（Verify）从磁盘复算长度与 SHA-256，并可核对清单签名与实际文件集合。
/// 纯只读：绝不写入、移动或删除任何数据文件。
/// </summary>
public sealed class BackupManifestBuilder
{
    public const string SessionsCategory = "sessions";
    public const string AttachmentsCategory = "attachments";

    private const string CodeTraversal = "PATH_TRAVERSAL";
    private const string CodeCrossRoot = "CROSS_ROOT";
    private const string CodeUnknownFile = "UNKNOWN_FILE";
    private const string CodeDuplicate = "DUPLICATE_ENTRY";
    private const string CodeMissing = "MISSING_FILE";
    private const string CodeUnreadable = "UNREADABLE_FILE";
    private const string CodeShaMismatch = "SHA_MISMATCH";
    private const string CodeLengthMismatch = "LENGTH_MISMATCH";
    private const string CodeSignature = "SIGNATURE_MISMATCH";
    private const string CodeSchema = "UNSUPPORTED_SCHEMA";
    private const string CodeInvalid = "INVALID_MANIFEST";

    /// <summary>
    /// 构建预期清单。<paramref name="root"/> 为数据根（如 .dsh 主目录）；
    /// <paramref name="sessions"/> 为选中会话（全部 Generations 物理文件计入）；
    /// <paramref name="attachmentPaths"/> 为附件绝对路径（须位于 root/attachments 下）。
    /// </summary>
    public ManifestBuildResult Build(
        string root,
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyList<string>? attachmentPaths = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var rootFull = NormalizeRoot(root);

        var issues = new List<ManifestIssue>();
        var entries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            ArgumentNullException.ThrowIfNull(session);
            foreach (var log in session.Generations)
            {
                ArgumentNullException.ThrowIfNull(log);
                AddFile(entries, issues, rootFull, log.FullPath, session.SessionId, BuildWarnings(session, log));
            }
        }

        if (attachmentPaths is not null)
        {
            foreach (var path in attachmentPaths)
            {
                AddFile(entries, issues, rootFull, path, null, []);
            }
        }

        if (issues.Count > 0)
        {
            return new ManifestBuildResult(null, issues);
        }

        var sorted = new List<ManifestEntry>(entries.Values);
        sorted.Sort(static (a, b) => string.CompareOrdinal(a.ZipPath, b.ZipPath));
        return new ManifestBuildResult(new BackupManifest(BackupManifest.CurrentSchemaVersion, rootFull, sorted), []);
    }

    /// <summary>
    /// 校验清单：结构复核 → 可选签名核对 → 逐条目从磁盘复算长度与 SHA-256 →
    /// 可选实际文件集合（出现清单外文件即 UNKNOWN_FILE）。
    /// </summary>
    public ManifestVerifyResult Verify(
        BackupManifest manifest,
        string root,
        IReadOnlyList<string>? actualFiles = null,
        string? expectedSignature = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<ManifestIssue>();
        var rootFull = NormalizeRoot(root);

        if (manifest.SchemaVersion != BackupManifest.CurrentSchemaVersion)
        {
            issues.Add(new ManifestIssue(CodeSchema, rootFull, $"不支持的 manifest schemaVersion={manifest.SchemaVersion}"));
            return new ManifestVerifyResult(issues);
        }

        if (expectedSignature is not null &&
            !string.Equals(expectedSignature, ComputeSignature(manifest), StringComparison.Ordinal))
        {
            issues.Add(new ManifestIssue(CodeSignature, rootFull, "清单签名与内容不一致"));
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var code = ValidateEntry(entry);
            if (code is not null)
            {
                issues.Add(new ManifestIssue(code, entry.ZipPath, $"条目非法：{entry.ZipPath}"));
                continue;
            }

            if (!known.Add(entry.ZipPath))
            {
                issues.Add(new ManifestIssue(CodeDuplicate, entry.ZipPath, $"重复条目：{entry.ZipPath}"));
                continue;
            }

            var full = Path.Combine(rootFull, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!IsUnderRoot(rootFull, full))
            {
                issues.Add(new ManifestIssue(CodeCrossRoot, entry.ZipPath, "条目解析到数据根之外"));
                continue;
            }

            if (!File.Exists(full))
            {
                issues.Add(new ManifestIssue(CodeMissing, entry.ZipPath, "文件缺失"));
                continue;
            }

            try
            {
                var (length, sha) = HashFile(full);
                if (length != entry.Length)
                {
                    issues.Add(new ManifestIssue(CodeLengthMismatch, entry.ZipPath, $"长度不符：期望 {entry.Length}，实际 {length}"));
                }

                if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
                {
                    issues.Add(new ManifestIssue(CodeShaMismatch, entry.ZipPath, "SHA-256 与磁盘复算不一致"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ManifestIssue(CodeUnreadable, entry.ZipPath, $"不可读：{ex.GetType().Name}"));
            }
        }

        if (actualFiles is not null)
        {
            foreach (var path in actualFiles)
            {
                var rel = TryNormalizeRelative(rootFull, path, out _);
                if (rel is null)
                {
                    issues.Add(new ManifestIssue(CodeUnknownFile, path, "实际文件无法归类到数据根内条目"));
                    continue;
                }

                if (!known.Contains(rel))
                {
                    issues.Add(new ManifestIssue(CodeUnknownFile, rel, "出现 manifest 之外的未知文件"));
                }
            }
        }

        return new ManifestVerifyResult(issues);
    }

    /// <summary>清单签名：对规范化 JSON（条目按 zipPath 排序、字段序固定）的 SHA-256 小写十六进制。</summary>
    public static string ComputeSignature(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = ToJson(manifest);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>序列化为规范化 JSON（紧凑、字段序固定、条目已排序）。</summary>
    public static string ToJson(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("root", manifest.Root);
            writer.WriteStartArray("entries");
            foreach (var entry in manifest.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("zipPath", entry.ZipPath);
                writer.WriteString("relativePath", entry.RelativePath);
                writer.WriteNumber("length", entry.Length);
                writer.WriteString("sha256", entry.Sha256);
                if (entry.SessionId is null)
                {
                    writer.WriteNull("sessionId");
                }
                else
                {
                    writer.WriteString("sessionId", entry.SessionId);
                }

                writer.WriteStartArray("warnings");
                foreach (var warning in entry.Warnings)
                {
                    writer.WriteStringValue(warning);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>解析并结构校验 manifest JSON；未知 schema、非法路径、非法 SHA 一律拒绝。</summary>
    public static ManifestLoadResult FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var issues = new List<ManifestIssue>();
        BackupManifest? manifest = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var rootEl = doc.RootElement;
            if (rootEl.ValueKind != JsonValueKind.Object)
            {
                return new ManifestLoadResult(null, [new ManifestIssue(CodeInvalid, "", "根不是 JSON 对象")]);
            }

            var schema = rootEl.TryGetProperty("schemaVersion", out var schemaEl) &&
                         schemaEl.ValueKind == JsonValueKind.Number
                ? schemaEl.GetInt32()
                : 0;
            if (schema != BackupManifest.CurrentSchemaVersion)
            {
                return new ManifestLoadResult(null, [new ManifestIssue(CodeSchema, "", $"schemaVersion={schema}")]);
            }

            var root = rootEl.TryGetProperty("root", out var rootEl2) && rootEl2.ValueKind == JsonValueKind.String
                ? rootEl2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(root))
            {
                return new ManifestLoadResult(null, [new ManifestIssue(CodeInvalid, "", "缺少 root")]);
            }

            var entries = new List<ManifestEntry>();
            if (rootEl.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entriesEl.EnumerateArray())
                {
                    var entry = ReadEntry(item);
                    if (entry is null)
                    {
                        return new ManifestLoadResult(null, [new ManifestIssue(CodeInvalid, "", "条目结构非法")]);
                    }

                    entries.Add(entry);
                }
            }

            manifest = new BackupManifest(schema, root!, entries);
            foreach (var entry in manifest.Entries)
            {
                var code = ValidateEntry(entry);
                if (code is not null)
                {
                    issues.Add(new ManifestIssue(code, entry.ZipPath, $"条目非法：{entry.ZipPath}"));
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (!seen.Add(entry.ZipPath))
                {
                    issues.Add(new ManifestIssue(CodeDuplicate, entry.ZipPath, $"重复条目：{entry.ZipPath}"));
                }
            }
        }
        catch (JsonException ex)
        {
            return new ManifestLoadResult(null, [new ManifestIssue(CodeInvalid, "", $"JSON 无效：{ex.Message}")]);
        }

        if (issues.Count > 0)
        {
            return new ManifestLoadResult(null, issues);
        }

        return new ManifestLoadResult(manifest, []);
    }

    // ---- 内部 ----

    private static void AddFile(
        Dictionary<string, ManifestEntry> entries,
        List<ManifestIssue> issues,
        string rootFull,
        string fullPath,
        string? sessionId,
        IReadOnlyList<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var rel = TryNormalizeRelative(rootFull, fullPath, out var rejectCode);
        if (rel is null)
        {
            issues.Add(new ManifestIssue(rejectCode!, fullPath, $"拒绝：{fullPath}"));
            return;
        }

        if (entries.ContainsKey(rel))
        {
            issues.Add(new ManifestIssue(CodeDuplicate, rel, $"重复条目：{rel}"));
            return;
        }

        var full = Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            issues.Add(new ManifestIssue(CodeMissing, rel, "文件缺失"));
            return;
        }

        try
        {
            var (length, sha) = HashFile(full);
            entries[rel] = new ManifestEntry(rel, rel, length, sha, sessionId, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new ManifestIssue(CodeUnreadable, rel, $"不可读：{ex.GetType().Name}"));
        }
    }

    private static IReadOnlyList<string> BuildWarnings(SessionSummary session, PhysicalLog log)
    {
        var warnings = new List<string>();
        if (session.SessionId is null)
        {
            warnings.Add("session-id-unresolved");
        }

        if (session.Status != ParseStatus.Valid)
        {
            warnings.Add($"session-status-{session.Status}");
        }

        if (log.Status != ParseStatus.Valid)
        {
            warnings.Add($"generation-status-{log.Status}");
        }

        return warnings;
    }

    /// <summary>
    /// 将绝对路径规范化为数据根内 '/' 分隔相对路径。
    /// 失败返回 null 并给出错误码：.. 段=PATH_TRAVERSAL；根外/跨盘=CROSS_ROOT；
    /// 类别非 sessions/attachments=UNKNOWN_FILE。
    /// </summary>
    private static string? TryNormalizeRelative(string rootFull, string fullPath, out string? rejectCode)
    {
        rejectCode = null;

        // 先于规范化检查原始路径：任何 .. 段（zip-slip 特征）一律拒绝，
        // 不依赖 GetFullPath 折叠语义。
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..")
            {
                rejectCode = CodeTraversal;
                return null;
            }
        }

        var full = Path.GetFullPath(fullPath);

        if (!IsUnderRoot(rootFull, full))
        {
            rejectCode = CodeCrossRoot;
            return null;
        }

        var rel = Path.GetRelativePath(rootFull, full).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var normalized = rel.Replace(Path.DirectorySeparatorChar, '/');
        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':'))
            {
                rejectCode = CodeInvalid;
                return null;
            }
        }

        if (!string.Equals(segments[0], SessionsCategory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(segments[0], AttachmentsCategory, StringComparison.OrdinalIgnoreCase))
        {
            rejectCode = CodeUnknownFile;
            return null;
        }

        return normalized;
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(full))
        {
            throw new ArgumentException("root 必须是绝对路径", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnderRoot(string rootFull, string full)
    {
        var rel = Path.GetRelativePath(rootFull, full);
        return !Path.IsPathFullyQualified(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static (long Length, string Sha256) HashFile(string full)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        var hash = SHA256.HashData(stream);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string? ValidateEntry(ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ZipPath) || string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            return CodeInvalid;
        }

        if (entry.ZipPath.Contains('\\') || entry.RelativePath.Contains('\\'))
        {
            return CodeInvalid;
        }

        if (entry.ZipPath != entry.RelativePath)
        {
            return CodeInvalid;
        }

        if (entry.ZipPath.StartsWith('/') || Path.IsPathFullyQualified(entry.ZipPath))
        {
            return CodeInvalid;
        }

        var segments = entry.ZipPath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':'))
            {
                return CodeInvalid;
            }
        }

        if (!string.Equals(segments[0], SessionsCategory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(segments[0], AttachmentsCategory, StringComparison.OrdinalIgnoreCase))
        {
            return CodeUnknownFile;
        }

        if (entry.Length < 0)
        {
            return CodeInvalid;
        }

        if (entry.Sha256 is null || entry.Sha256.Length != 64 || !IsLowerHex(entry.Sha256))
        {
            return CodeInvalid;
        }

        return null;
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static ManifestEntry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var zipPath = GetString(item, "zipPath");
        var relativePath = GetString(item, "relativePath");
        var sha = GetString(item, "sha256");
        if (zipPath is null || relativePath is null || sha is null)
        {
            return null;
        }

        if (!item.TryGetProperty("length", out var lengthEl) || lengthEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        long length;
        try
        {
            length = lengthEl.GetInt64();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        string? sessionId = null;
        if (item.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
        {
            sessionId = sidEl.GetString();
        }

        var warnings = new List<string>();
        if (item.TryGetProperty("warnings", out var warningsEl) && warningsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in warningsEl.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.String && w.GetString() is { } text)
                {
                    warnings.Add(text);
                }
            }
        }

        return new ManifestEntry(zipPath, relativePath, length, sha, sessionId, warnings);
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
