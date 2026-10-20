using System.Globalization;
using System.Text.Json;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 预览条目（design.md §1 草案 PreviewItem）：Text 为纯文本，UI 侧禁止渲染 Markdown/HTML。
/// IsPartial=true 表示该事件不完整（缺字段、非文本、未知 schema、无效 JSON），Text 为摘要而非正文。
/// </summary>
public sealed record PreviewItem(string Role, string Text, DateTimeOffset? At, bool IsPartial)
{
    public bool IsUnsupported { get; init; }
}

/// <summary>投影结果：单次预览 ≤2000 条（design.md §34），超限置 Truncated，不假装解析完整。</summary>
public sealed record TranscriptProjection(IReadOnlyList<PreviewItem> Items, bool Truncated, int IncompleteCount)
{
    public bool IsProjectionComplete => !Truncated && IncompleteCount == 0;
    public int UnsupportedCount => Items.Count(x => x.IsUnsupported);
}

/// <summary>
/// T-13 V4 可验证消息预览：仅将脱敏 fixture 确证的用户/助手文本事件（design.md §48）
/// 转成 PreviewItem；未知事件输出类型/行号与“不完整”摘要，不猜测语义、不展示推理/密钥字段。
/// 输入为已解码的 JSONL 文本（事件流→Preview；不执行 Harness 事件迁移）。
/// </summary>
public sealed class TranscriptProjector
{
    public const int MaxPreviewItems = 2_000;
    public const string UnknownRole = "(unknown)";

    /// <summary>由 FixtureFactory.MessageLine 脱敏 fixture 断言绑定的确证角色。</summary>
    private static readonly string[] ConfirmedMessageRoles = ["user", "assistant"];

    /// <summary>确证的已知非消息事件：预览中跳过，不作为不完整。</summary>
    private static readonly string[] SkippedKnownTypes = ["header", "session", "attachment_ref", "session/title",
        "assistant/chunk", "assistant/attempt", "text-chunks", "reasoning-chunks", "tool-call-chunks"];

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
            if (items.Count > maxItems)
            {
                items.RemoveRange(maxItems, items.Count - maxItems);
                truncated = true;
                break;
            }
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

        if (SkippedKnownTypes.Contains(type, StringComparer.Ordinal) || NativeEventKinds.Metadata.Contains(type))
        {
            return;
        }

        if (type is "user/message" or "assistant/message" or "tool/result")
        {
            ProjectNativeMessage(root, type, lineNumber, items);
            return;
        }

        if (type == "tool/call")
        {
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && GetString(data, "name") is { } name)
                items.Add(new PreviewItem("tool", $"调用工具：{name}（参数未展开）", null, false));
            else items.Add(Incomplete(lineNumber, "工具调用缺少名称"));
            return;
        }
        if (type is "system/message" or "developer/message")
        {
            items.Add(new PreviewItem("system", "系统或开发者消息（内部正文未展开）", null, false));
            return;
        }

        if (string.Equals(type, "message", StringComparison.Ordinal))
        {
            var role = GetString(root, "role");
            var text = GetString(root, "text");
            if (role is not null
                && ConfirmedMessageRoles.Contains(role, StringComparer.Ordinal)
                && text is not null)
            {
                DateTimeOffset? at = null;
                var ts = GetString(root, "ts");
                if (ts is not null
                    && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    at = parsed;
                }

                items.Add(new PreviewItem(role, text, at, IsPartial: false));
            }
            else
            {
                // 缺字段、非文本正文或未确证角色：不猜测语义，标记不完整。
                items.Add(Incomplete(lineNumber, "消息缺字段、非文本或未确证角色"));
            }

            return;
        }

        items.Add(new PreviewItem(UnknownRole, $"未支持事件：第 {lineNumber} 行（未知事件类型 {type}；不表示原始数据损坏）", null, true) { IsUnsupported = true });
    }

    private static void ProjectNativeMessage(JsonElement root, string type, int lineNumber, List<PreviewItem> items)
    {
        var role = type == "user/message" ? "user" : type == "tool/result" ? "tool" : "assistant";
        if (!root.TryGetProperty("data", out var message) || message.ValueKind != JsonValueKind.Object
            || (role != "user" && (!message.TryGetProperty("message", out message) || message.ValueKind != JsonValueKind.Object))
            || (GetString(message, "role") != role && !(role == "tool" && GetString(message, "role") == "user"))
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            items.Add(Incomplete(lineNumber, "消息缺字段、非文本或角色不匹配"));
            return;
        }

        // V0 stores a tool result as role=user with one tool-result wrapper. V4 uses role=tool.
        // Both structures are corroborated by local physical logs and the historical catalog.
        if (role == "tool" && GetString(message, "role") == "user")
        {
            if (content.GetArrayLength() != 1 || content[0].ValueKind != JsonValueKind.Object
                || GetString(content[0], "type") != "tool-result" || GetString(content[0], "toolCallId") is null
                || !content[0].TryGetProperty("content", out var nested) || nested.ValueKind != JsonValueKind.Array)
            {
                items.Add(Incomplete(lineNumber, "旧版工具结果缺少必要字段"));
                return;
            }
            content = nested;
        }

        DateTimeOffset? at = null;
        if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
            && time.TryGetInt64(out var milliseconds) && milliseconds >= 0
            && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

        var text = new List<string>();
        var partial = false;
        var unsupported = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) { partial = true; continue; }
            var blockType = GetString(block, "type");
            if (blockType == "text" && GetString(block, "text") is { } value)
                text.Add(value);
            else if (blockType is "image" or "file" && block.TryGetProperty("attachment", out var attachment)
                && attachment.ValueKind == JsonValueKind.Object && GetString(attachment, "attachmentId") is not null)
                text.Add(blockType == "image" ? "[图片附件，见附件页]" : "[文件附件，见附件页]");
            else if (blockType is "text" or "image" or "file" || blockType is null)
                partial = true;
            else if (blockType is not ("reasoning" or "tool-call"))
                unsupported = true;
        }

        if (text.Count > 0)
            items.Add(new PreviewItem(role, string.Join("\n", text), at, IsPartial: false));
        if (partial)
            items.Add(Incomplete(lineNumber, "内容块缺少必要字段或类型错误"));
        if (unsupported)
            items.Add(new PreviewItem(UnknownRole, $"未支持内容块：第 {lineNumber} 行（未展示，不表示数据损坏）", at, true) { IsUnsupported = true });
    }

    private static PreviewItem Incomplete(int lineNumber, string reason) =>
        new(UnknownRole, $"不完整事件：第 {lineNumber} 行（{reason}）", null, IsPartial: true);

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
