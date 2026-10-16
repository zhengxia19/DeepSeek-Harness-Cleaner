using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>附件引用证据状态（T-15）。</summary>
public enum AttachmentEvidenceStatus
{
    /// <summary>至少一个已验证 attachment_ref 事件引用该附件。</summary>
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
    IReadOnlyList<string> ReferencingSessionIds);

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
/// 附件引用证据索引（T-15，design.md §5）。仅从已验证 attachment_ref 事件建立引用；
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

            foreach (var id in ExtractAttachmentIds(session.TranscriptText))
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
            attachments.Add(new AttachmentEvidence(id, status, refs));
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
    private static List<string> ExtractAttachmentIds(string transcriptText)
    {
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
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String
                    || !string.Equals(typeElement.GetString(), "attachment_ref", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("attachmentId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
            catch (JsonException)
            {
                // 无效 JSON：不从未验证事件建立引用（design.md §5）。
            }
        }

        return ids;
    }
}
