using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>附件引用证据状态（T-15）。</summary>
public enum AttachmentEvidenceStatus
{
    /// <summary>至少一个已验证 attachment_ref 或原生 image/file 内容块引用该附件。</summary>
    Referenced,

    /// <summary>无已验证引用，但全局解析覆盖不完整：不可判定孤儿（design.md §5）。</summary>
    Unknown,

    /// <summary>全局覆盖完整且无任何已验证引用；仍不自动删除（T-29 显式处理）。</summary>
    ConfirmedUnreferenced,
}

/// <summary>单个磁盘附件的引用证据。</summary>
public sealed record AttachmentEvidence(
    string AttachmentId,
    AttachmentEvidenceStatus Status,
    IReadOnlyList<string> ReferencingSessionIds)
{
    public IReadOnlyList<string> DisplayNames { get; init; } = [];
    public IReadOnlyList<string> TypeLabels { get; init; } = [];
}

/// <summary>引用了磁盘上不存在附件 ID 的悬空引用（仅证据，不虚构附件条目）。</summary>
public sealed record DanglingAttachmentReference(
    string AttachmentId,
    IReadOnlyList<string> SessionIds);

/// <summary>
/// 会话附件证据输入。<paramref name="TranscriptText"/> 为调用方已解码的 JSONL 文本
/// （不可读时传 null）；仅 Status=Valid 且文本非空的会话计入全局覆盖。
/// </summary>
public sealed record SessionAttachmentSource(
    string SessionId,
    string? TranscriptText,
    ParseStatus Status);

/// <summary>附件引用索引结果。GlobalOrphanConclusionAvailable=false 时禁止任何孤儿结论。</summary>
public sealed record AttachmentIndexResult(
    IReadOnlyList<AttachmentEvidence> Attachments,
    IReadOnlyList<DanglingAttachmentReference> DanglingReferences,
    bool GlobalOrphanConclusionAvailable,
    IReadOnlyList<string> UnresolvedSessionIds);

/// <summary>
/// 附件引用证据索引（T-15，design.md §5）。从已验证 attachment_ref 与原生内容块建立正向引用；
/// 任一未解析会话使全局孤儿结论不可得（Unknown，而非孤儿）；纯只读，绝不删除附件。
/// 调用方须传入完整会话集合与白名单验证后的磁盘附件 ID 清单。
/// </summary>
public sealed class AttachmentIndex
{
    public AttachmentIndexResult Build(
        IReadOnlyList<SessionAttachmentSource> sessions,
        IReadOnlyList<string> attachmentIdsOnDisk)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(attachmentIdsOnDisk);

        var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var display = new Dictionary<string, List<(string? Name, string Type)>>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var allResolved = sessions.Count > 0;

        foreach (var session in sessions)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (session.Status != ParseStatus.Valid || string.IsNullOrEmpty(session.TranscriptText))
            {
                allResolved = false;
                if (!unresolved.Contains(session.SessionId, StringComparer.Ordinal))
                {
                    unresolved.Add(session.SessionId);
                }
            }

            if (string.IsNullOrEmpty(session.TranscriptText))
            {
                continue;
            }

            var attachmentIds = ExtractAttachmentIds(session.TranscriptText, out var complete, out var metadata);
            foreach (var item in metadata)
            {
                if (!display.TryGetValue(item.Id, out var entries)) display[item.Id] = entries = [];
                entries.Add((item.Name, item.Type));
            }
            foreach (var id in attachmentIds)
            {
                if (!references.TryGetValue(id, out var list))
                {
                    list = [];
                    references[id] = list;
                }

                if (!list.Contains(session.SessionId, StringComparer.Ordinal))
                {
                    list.Add(session.SessionId);
                }
            }
            if (!complete)
            {
                allResolved = false;
                if (!unresolved.Contains(session.SessionId, StringComparer.Ordinal)) unresolved.Add(session.SessionId);
            }
        }

        var diskIds = new List<string>();
        foreach (var id in attachmentIdsOnDisk)
        {
            if (!string.IsNullOrWhiteSpace(id) && !diskIds.Contains(id, StringComparer.Ordinal))
            {
                diskIds.Add(id);
            }
        }

        var attachments = new List<AttachmentEvidence>(diskIds.Count);
        foreach (var id in diskIds)
        {
            var refs = references.TryGetValue(id, out var list) ? list : [];
            var status = refs.Count > 0
                ? AttachmentEvidenceStatus.Referenced
                : allResolved
                    ? AttachmentEvidenceStatus.ConfirmedUnreferenced
                    : AttachmentEvidenceStatus.Unknown;
            display.TryGetValue(id, out var labels);
            attachments.Add(new AttachmentEvidence(id, status, refs)
            {
                DisplayNames = labels?.Select(x => x.Name).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [],
                TypeLabels = labels?.Select(x => x.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [],
            });
        }

        var dangling = new List<DanglingAttachmentReference>();
        foreach (var pair in references)
        {
            if (!diskIds.Contains(pair.Key, StringComparer.Ordinal))
            {
                dangling.Add(new DanglingAttachmentReference(pair.Key, pair.Value));
            }
        }

        attachments.Sort((a, b) => string.CompareOrdinal(a.AttachmentId, b.AttachmentId));
        dangling.Sort((a, b) => string.CompareOrdinal(a.AttachmentId, b.AttachmentId));
        unresolved.Sort(StringComparer.Ordinal);

        return new AttachmentIndexResult(attachments, dangling, allResolved, unresolved);
    }

    /// <summary>
    /// 仅提取已验证 attachment_ref 事件的 attachmentId：JSON 对象、type 精确匹配、
    /// attachmentId 为非空字符串。坏 JSON/未知事件/缺字段/非字符串一律不建立引用。
    /// </summary>
    private static List<string> ExtractAttachmentIds(string transcriptText, out bool complete,
        out List<(string Id, string? Name, string Type)> metadata)
    {
        metadata = [];
        complete = true;
        var ids = new List<string>();
        foreach (var raw in transcriptText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    complete = false;
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    complete = false;
                    continue;
                }

                var type = typeElement.GetString();
                if (type is "user/message" or "assistant/message" or "tool/result")
                {
                    complete = false; // Native replay/plugin/old-format coverage is not certified globally.
                    if (!doc.RootElement.TryGetProperty("data", out var message) || message.ValueKind != JsonValueKind.Object)
                        continue;
                    if (type != "user/message" && (!message.TryGetProperty("message", out message) || message.ValueKind != JsonValueKind.Object))
                        continue;
                    var expectedRole = type == "user/message" ? "user" : type == "tool/result" ? "tool" : "assistant";
                    if (!message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                        continue;
                    if (type == "tool/result" && role.GetString() == "user")
                    {
                        if (message.TryGetProperty("content", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array && wrapped.GetArrayLength() == 1
                            && wrapped[0].ValueKind == JsonValueKind.Object && wrapped[0].TryGetProperty("type", out var wrapperType)
                            && wrapperType.ValueKind == JsonValueKind.String && wrapperType.GetString() == "tool-result"
                            && wrapped[0].TryGetProperty("toolCallId", out var callId) && callId.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(callId.GetString())
                            && wrapped[0].TryGetProperty("content", out var nested) && nested.ValueKind == JsonValueKind.Array)
                            ExtractContent(nested, ids, metadata);
                        continue;
                    }
                    if (role.GetString() != expectedRole) continue;
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        ExtractContent(content, ids, metadata);
                    continue;
                }
                if (type != "attachment_ref")
                {
                    if (type is not ("header" or "message")) complete = false;
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("attachmentId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    complete = false;
                    continue;
                }

                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
                else complete = false;
            }
            catch (JsonException)
            {
                complete = false;
                // 无效 JSON：不从未验证事件建立引用（design.md §5）。
            }
        }

        return ids;
    }

    private static void ExtractContent(JsonElement content, List<string> ids, List<(string Id, string? Name, string Type)> metadata)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("image" or "file")
                || !block.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object
                || !attachment.TryGetProperty("attachmentId", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())) continue;
            ids.Add(id.GetString()!);
            string? name = null;
            if (attachment.TryGetProperty("name", out var displayName) && displayName.ValueKind == JsonValueKind.String)
            {
                var value = displayName.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Any(char.IsControl)
                    && value.IndexOfAny(['/', '\\', ':']) < 0) name = value;
            }
            var label = type.GetString() == "image" ? "图片（格式未知）" : "文件";
            if (type.GetString() == "image" && attachment.TryGetProperty("mediaType", out var media) && media.ValueKind == JsonValueKind.String
                && media.GetString() is "image/png" or "image/jpeg" or "image/webp" or "image/gif")
                label = "图片（" + media.GetString() + "）";
            metadata.Add((id.GetString()!, name, label));
        }
    }
}
