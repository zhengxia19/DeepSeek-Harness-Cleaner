using System.Globalization;
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
    private static readonly string[] SkippedKnownTypes = ["header", "attachment_ref"];

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

        items.Add(Incomplete(lineNumber, $"未知事件类型 {type}"));
    }

    private static PreviewItem Incomplete(int lineNumber, string reason) =>
        new(UnknownRole, $"不完整事件：第 {lineNumber} 行（{reason}）", null, IsPartial: true);

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
