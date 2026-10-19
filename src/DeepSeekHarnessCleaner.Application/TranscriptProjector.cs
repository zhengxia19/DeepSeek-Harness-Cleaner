using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 预览条目（design.md §1 草案 PreviewItem）：Text 为纯文本，UI 侧禁止渲染 Markdown/HTML。
/// IsPartial=true 表示该事件不完整（缺字段、非文本、未知 schema、无效 JSON），Text 为摘要而非正文。
/// </summary>
public sealed record PreviewItem(string Role, string Text, DateTimeOffset? At, bool IsPartial);

/// <summary>投影结果：单次预览 ≤2000 条（design.md §34），超限置 Truncated，不假装解析完整。</summary>
public sealed record TranscriptProjection(IReadOnlyList<PreviewItem> Items, bool Truncated, int IncompleteCount)
{
    public bool IsProjectionComplete => !Truncated && IncompleteCount == 0;
}

/// <summary>
/// T-13 消息预览（M-03 起按真实事件信封映射）：信封为
/// <c>{"type":&lt;name&gt;,"seq":&lt;num&gt;,"time":&lt;epoch-millis&gt;,"data":{…}}</c>（M-01 证据 DIAG-event-format.txt）。
/// 仅将真实数据确证的消息事件（<c>user/message</c> 等；正文在 <c>data</c> 或嵌套 <c>data.message</c>
/// 的 role + content[] 文本块）与 <c>tool/result</c>（正文在 <c>data.message</c>）转成 PreviewItem；
/// 未投影事件输出类型/行号与“不完整”摘要，不猜测语义、不展示推理/密钥字段。
/// 输入为已解码的 JSONL 文本（事件流→Preview；不执行 Harness 事件迁移）。
/// </summary>
public sealed class TranscriptProjector
{
    public const int MaxPreviewItems = 2_000;
    public const string UnknownRole = "(unknown)";

    /// <summary>真实数据确证的消息角色（M-01：role 取值集合 user/assistant/system/developer/tool）。</summary>
    private static readonly string[] ConfirmedMessageRoles = ["user", "assistant", "system", "developer"];

    /// <summary>真实数据确证的消息事件类型；正文在 data（user/message）或 data.message（其余三者）。</summary>
    private static readonly string[] MessageEventTypes =
        ["user/message", "assistant/message", "system/message", "developer/message"];

    /// <summary>
    /// 确证的已知非消息事件：预览中跳过，不作为不完整。
    /// session/attachment_ref 为元数据/附件引用；三个聚合 chunk 行（seq0/time0 + data.dt[]/data.texts[]，M-01）
    /// 为流式增量片段，正文由最终消息事件承载，预览不投影片段以免重复。
    /// </summary>
    private static readonly string[] SkippedKnownTypes =
        ["session", "attachment_ref", "reasoning-chunks", "tool-call-chunks", "text-chunks"];

    public TranscriptProjection Project(
        string transcriptText,
        int maxItems = MaxPreviewItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriptText);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);

        var items = new List<PreviewItem>();
        var truncated = false;
        var lineNumber = 0;
        var rest = transcriptText.AsSpan();

        while (true)
        {
            var nl = rest.IndexOf('\n');
            var line = nl >= 0 ? rest[..nl] : rest;
            if (nl < 0 && line.IsEmpty)
            {
                break; // 末尾换行不产生幻影行
            }

            lineNumber++;
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            ProjectLine(line, lineNumber, items);
            if (nl < 0)
            {
                break;
            }

            rest = rest[(nl + 1)..];
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count >= maxItems && !rest.IsEmpty)
            {
                truncated = true;
                break;
            }
        }

        var incomplete = items.Count(static item => item.IsPartial);
        return new TranscriptProjection(items, truncated, incomplete);
    }

    private static void ProjectLine(ReadOnlySpan<char> line, int lineNumber, List<PreviewItem> items)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.ToString());
            ProjectParsed(doc.RootElement, lineNumber, items);
        }
        catch (JsonException)
        {
            items.Add(Incomplete(lineNumber, "无效 JSON"));
        }
    }

    private static void ProjectParsed(JsonElement root, int lineNumber, List<PreviewItem> items)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            items.Add(Incomplete(lineNumber, "非 JSON 对象"));
            return;
        }

        var type = GetString(root, "type");
        if (string.IsNullOrEmpty(type))
        {
            items.Add(Incomplete(lineNumber, "缺少 type 字段"));
            return;
        }

        if (SkippedKnownTypes.Contains(type, StringComparer.Ordinal))
        {
            return;
        }

        if (MessageEventTypes.Contains(type, StringComparer.Ordinal))
        {
            // 真实信封（M-03 msg-probe 确证）：user/message 正文在 data（role+content[]）；
            // assistant/system/developer/message 正文嵌套在 data.message（同 tool/result 布局）。
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var owner = data.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : data;
                var role = GetString(owner, "role");
                var text = ExtractTextBlocks(owner);
                if (role is not null
                    && ConfirmedMessageRoles.Contains(role, StringComparer.Ordinal)
                    && text is not null)
                {
                    items.Add(new PreviewItem(role, text, GetEpochMillis(root, "time"), IsPartial: false));
                }
                else
                {
                    // 缺字段、非文本正文（如仅 tool-call/reasoning 块）或未确证角色：不猜测语义，标记不完整。
                    items.Add(Incomplete(lineNumber, "消息缺字段、非文本或未确证角色"));
                }
            }
            else
            {
                items.Add(Incomplete(lineNumber, "消息缺字段、非文本或未确证角色"));
            }

            return;
        }

        if (string.Equals(type, "tool/result", StringComparison.Ordinal))
        {
            // 真实信封：工具结果正文在 data.message（role="tool"，content[] 文本块，M-01）。
            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && GetString(message, "role") == "tool"
                && ExtractTextBlocks(message) is string text)
            {
                items.Add(new PreviewItem("tool", text, GetEpochMillis(root, "time"), IsPartial: false));
            }
            else
            {
                items.Add(Incomplete(lineNumber, "工具结果缺字段或非文本"));
            }

            return;
        }

        items.Add(Incomplete(lineNumber, $"未知事件类型 {type}"));
    }

    private static PreviewItem Incomplete(int lineNumber, string reason) =>
        new(UnknownRole, $"不完整事件：第 {lineNumber} 行（{reason}）", null, IsPartial: true);

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>连接 content[] 中全部 {type:"text"} 文本块（\n 分隔）；无文本块返回 null（不猜测非文本语义）。</summary>
    private static string? ExtractTextBlocks(JsonElement owner)
    {
        if (!owner.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var joined = new StringBuilder();
        var textBlocks = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && GetString(block, "type") == "text"
                && GetString(block, "text") is string text)
            {
                if (textBlocks > 0)
                {
                    joined.Append('\n');
                }

                joined.Append(text);
                textBlocks++;
            }
        }

        return textBlocks > 0 ? joined.ToString() : null;
    }

    /// <summary>信封 time 字段：epoch-millis 数值（M-01：ISO 字符串 0 次出现）；缺失/越界按 null 时间处理。</summary>
    private static DateTimeOffset? GetEpochMillis(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var millis))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}
