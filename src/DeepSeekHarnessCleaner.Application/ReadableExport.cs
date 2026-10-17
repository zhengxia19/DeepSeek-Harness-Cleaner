using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 可读导出输入。<paramref name="TranscriptText"/> 必须是
/// <see cref="LegacyPreview.SelectPreviewGeneration"/> 选定代际的已解码文本（不可读传 null）；
/// <paramref name="AttachmentIdsOnDisk"/> 为 null 表示磁盘附件清单不完整（状态不可判定）。
/// </summary>
public sealed record ReadableExportInput(
    SessionSummary Session,
    string? TranscriptText,
    IReadOnlyList<string>? ReferencedAttachmentIds = null,
    IReadOnlyCollection<string>? AttachmentIdsOnDisk = null);

/// <summary>
/// 可读导出结果（interfaces.md §33）。Markdown 与 JSON 均为 UTF-8 文本；
/// ExportIncomplete=true 当且仅当存在未知事件/超限截断/附件缺口或状态未知/仅元数据模式。
/// 可读导出不是恢复备份。
/// </summary>
public sealed record ReadableExportResult(
    string Markdown,
    string Json,
    bool ExportIncomplete,
    IReadOnlyList<string> IncompleteReasons);

/// <summary>
/// Markdown/JSON 可读导出（T-23，TC-10）。离线导出可读消息并标记事件/附件缺口；
/// 不联网、不猜测未知语义、不作为恢复凭据。Markdown 代码块围栏按内容中最长反引号串
/// 加长，保证含 ```、~~~、中文、换行的正文不破坏结构；JSON 用宽松转义保留 UTF-8 中文。
/// </summary>
public sealed class ReadableExport
{
    private const int JsonSchemaVersion = 1;

    /// <summary>生成可读导出。纯内存字符串变换，无任何 IO/网络访问。</summary>
    public ReadableExportResult Export(ReadableExportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Session);

        var legacy = new LegacyPreview().Build(input.Session, input.TranscriptText);
        var reasons = new List<string>();
        var projection = legacy.Projection;

        if (legacy.Mode == LegacyPreviewMode.MetadataOnly)
        {
            reasons.Add(legacy.Reason);
        }

        if (projection is not null)
        {
            if (projection.Truncated)
            {
                reasons.Add($"投影达到 {TranscriptProjector.MaxPreviewItems} 条上限被截断（超限）");
            }

            if (projection.IncompleteCount > 0)
            {
                reasons.Add($"含 {projection.IncompleteCount} 个不完整/未知事件标记");
            }
        }

        var attachments = BuildAttachmentStates(input, reasons);
        var exportIncomplete = reasons.Count > 0;
        var markdown = BuildMarkdown(legacy, projection, attachments, exportIncomplete, reasons);
        var json = BuildJson(input.Session, projection, attachments, exportIncomplete, reasons);
        return new ReadableExportResult(markdown, json, exportIncomplete, reasons);
    }

    // ---- 附件缺口 ----

    private static IReadOnlyList<(string Id, string State)> BuildAttachmentStates(
        ReadableExportInput input,
        List<string> reasons)
    {
        var referenced = new List<string>();
        if (input.ReferencedAttachmentIds is not null)
        {
            foreach (var id in input.ReferencedAttachmentIds)
            {
                if (!string.IsNullOrWhiteSpace(id) && !referenced.Contains(id, StringComparer.Ordinal))
                {
                    referenced.Add(id);
                }
            }
        }

        referenced.Sort(StringComparer.Ordinal);
        if (referenced.Count == 0)
        {
            return [];
        }

        var states = new List<(string, string)>(referenced.Count);
        if (input.AttachmentIdsOnDisk is null)
        {
            reasons.Add("附件磁盘清单不完整：引用附件存在性不可判定");
            foreach (var id in referenced)
            {
                states.Add((id, "unknown"));
            }

            return states;
        }

        foreach (var id in referenced)
        {
            if (input.AttachmentIdsOnDisk.Contains(id, StringComparer.Ordinal))
            {
                states.Add((id, "present"));
            }
            else
            {
                states.Add((id, "missing"));
                reasons.Add($"附件缺失：{id}");
            }
        }

        return states;
    }

    // ---- Markdown ----

    private static string BuildMarkdown(
        LegacyPreviewResult legacy,
        TranscriptProjection? projection,
        IReadOnlyList<(string Id, string State)> attachments,
        bool exportIncomplete,
        IReadOnlyList<string> reasons)
    {
        var sb = new StringBuilder();
        sb.Append("# 会话可读导出（不是恢复备份）\n\n");
        sb.Append("> 本导出仅供阅读参考，**不是恢复备份**；原始字节与全部代际见 backup ZIP。\n\n");
        sb.Append($"- exportIncomplete: {(exportIncomplete ? "true" : "false")}\n");
        foreach (var reason in reasons)
        {
            sb.Append($"- 不完整原因：{reason}\n");
        }

        sb.Append("\n## 元数据\n\n");
        foreach (var line in legacy.MetadataLines)
        {
            sb.Append("- ").Append(line).Append('\n');
        }

        sb.Append("\n## 消息\n\n");
        if (projection is null)
        {
            sb.Append("（无已确证的事件投影，见上方不完整原因）\n");
        }
        else
        {
            foreach (var item in projection.Items)
            {
                if (item.IsPartial)
                {
                    sb.Append("> ").Append(item.Text).Append("\n\n");
                    continue;
                }

                var at = item.At?.ToString("O", CultureInfo.InvariantCulture) ?? "(无时间戳)";
                sb.Append("### ").Append(item.Role).Append(" · ").Append(at).Append("\n\n");
                var fence = MakeFence(item.Text);
                sb.Append(fence).Append('\n').Append(item.Text).Append('\n').Append(fence).Append("\n\n");
            }
        }

        sb.Append("## 附件\n\n");
        if (attachments.Count == 0)
        {
            sb.Append("（无引用附件）\n");
        }
        else
        {
            foreach (var (id, state) in attachments)
            {
                var label = state switch
                {
                    "present" => "存在",
                    "missing" => "缺失",
                    _ => "状态未知",
                };
                sb.Append("- ").Append(id).Append('：').Append(label).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>围栏长度 = 内容中最长反引号串 + 1（至少 3），保证正文不提前闭合代码块。</summary>
    private static string MakeFence(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            if (c == '`')
            {
                run++;
                if (run > longest)
                {
                    longest = run;
                }
            }
            else
            {
                run = 0;
            }
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    // ---- JSON ----

    private static string BuildJson(
        SessionSummary session,
        TranscriptProjection? projection,
        IReadOnlyList<(string Id, string State)> attachments,
        bool exportIncomplete,
        IReadOnlyList<string> reasons)
    {
        var options = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        };

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", JsonSchemaVersion);
        WriteStringOrNull(writer, "sessionId", session.SessionId);
        WriteStringOrNull(writer, "project", session.Project);
        WriteDate(writer, "createdAt", session.CreatedAt);
        WriteDate(writer, "lastActivityAt", session.LastActivityAt);
        writer.WriteString("status", session.Status.ToString());
        writer.WriteBoolean("exportIncomplete", exportIncomplete);

        writer.WriteStartArray("incompleteReasons");
        foreach (var reason in reasons)
        {
            writer.WriteStringValue(reason);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("generations");
        foreach (var log in session.Generations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("generation", log.Generation);
            writer.WriteBoolean("compressed", log.Compressed);
            writer.WriteNumber("bytes", log.Bytes);
            WriteDate(writer, "modifiedAt", log.ModifiedAt);
            writer.WriteString("status", log.Status.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("attachments");
        foreach (var (id, state) in attachments)
        {
            writer.WriteStartObject();
            writer.WriteString("attachmentId", id);
            writer.WriteString("state", state);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("projection");
        if (projection is null)
        {
            writer.WriteNull("items");
            writer.WriteBoolean("truncated", false);
            writer.WriteNumber("incompleteCount", 0);
        }
        else
        {
            writer.WriteStartArray("items");
            foreach (var item in projection.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("role", item.Role);
                writer.WriteString("text", item.Text);
                WriteDate(writer, "at", item.At);
                writer.WriteBoolean("isPartial", item.IsPartial);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("truncated", projection.Truncated);
            writer.WriteNumber("incompleteCount", projection.IncompleteCount);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            writer.WriteString(property, value);
        }
    }

    private static void WriteDate(Utf8JsonWriter writer, string property, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            writer.WriteString(property, value.Value.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
