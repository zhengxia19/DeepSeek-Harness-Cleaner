using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-23：Markdown/JSON 可读导出（TC-10）：UTF-8 中文/换行/代码块结构安全、exportIncomplete 标记、离线。</summary>
public sealed class ReadableExportTests
{
    private static SessionSummary Session(int generation) =>
        new(
            "synth-exp-001",
            "proj-exp",
            new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
            null,
            DateOrigin.Header,
            [new PhysicalLog(@"C:\synth\session.jsonl", generation, false, 123, DateTimeOffset.UnixEpoch, null, ParseStatus.Valid)],
            ParseStatus.Valid)
        { TotalBytes = 123 };

    private static string Header(int formatVersion) =>
        FixtureFactory.HeaderLine("synth-exp-001", formatVersion, "2026-01-05T09:00:00Z");

    [Fact]
    public void Export_ChineseNewlinesAndFences_MarkdownRoundTrips()
    {
        // 正文含中文、换行、``` 与 4 连反引号、~~~；JSON 转义 \n 解码后为真实换行。
        var text = "你好\n世界\n```\ncode\n````\n~~~";
        var transcript = string.Join(
            '\n',
            Header(4),
            "{\"type\":\"message\",\"role\":\"user\",\"text\":\"你好\\n世界\\n```\\ncode\\n````\\n~~~\",\"ts\":\"2026-01-05T09:01:00Z\"}",
            FixtureFactory.MessageLine("assistant", "回复 inline ``` end", "2026-01-05T09:02:00Z")) + "\n";

        var result = new ReadableExport().Export(new ReadableExportInput(Session(4), transcript));

        Assert.False(result.ExportIncomplete);
        Assert.StartsWith("# 会话可读导出（不是恢复备份）", result.Markdown);
        Assert.Contains("不是恢复备份", result.Markdown);
        Assert.Contains("你好", result.Markdown);

        var first = ExtractFenced(result.Markdown, "### user · ");
        var second = ExtractFenced(result.Markdown, "### assistant · ");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(text, first);
        Assert.Equal("回复 inline ``` end", second);
    }

    [Fact]
    public void Export_UnknownEvent_MarksIncompleteInBothOutputs()
    {
        var transcript = string.Join(
            '\n',
            Header(4),
            FixtureFactory.MessageLine("user", "正常消息", "2026-01-05T09:01:00Z"),
            FixtureFactory.UnknownEventLine) + "\n";

        var result = new ReadableExport().Export(new ReadableExportInput(Session(4), transcript));

        Assert.True(result.ExportIncomplete);
        Assert.Contains(result.IncompleteReasons, r => r.Contains("不完整/未知事件"));
        Assert.Contains("> 不完整事件：第 3 行", result.Markdown);

        using var doc = JsonDocument.Parse(result.Json);
        Assert.True(doc.RootElement.GetProperty("exportIncomplete").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("incompleteReasons").GetArrayLength());
    }

    [Fact]
    public void Export_TruncatedAtPreviewLimit_MarksIncomplete()
    {
        var sb = new StringBuilder();
        sb.Append(Header(4)).Append('\n');
        for (var i = 0; i < TranscriptProjector.MaxPreviewItems + 1; i++)
        {
            sb.Append("{\"type\":\"message\",\"role\":\"user\",\"text\":\"m").Append(i).Append("\"}\n");
        }

        var result = new ReadableExport().Export(new ReadableExportInput(Session(4), sb.ToString()));

        Assert.True(result.ExportIncomplete);
        Assert.Contains(result.IncompleteReasons, r => r.Contains("超限"));
    }

    [Fact]
    public void Export_AttachmentMissingAndUnknownStates_Marked()
    {
        var transcript = string.Join('\n', Header(4), FixtureFactory.AttachmentRefLine("att-1"), FixtureFactory.AttachmentRefLine("att-2")) + "\n";

        var missing = new ReadableExport().Export(new ReadableExportInput(
            Session(4), transcript, ["att-1", "att-2"], ["att-1"]));
        Assert.True(missing.ExportIncomplete);
        Assert.Contains("附件缺失：att-2", missing.IncompleteReasons);
        Assert.Contains("att-1：存在", missing.Markdown);
        Assert.Contains("att-2：缺失", missing.Markdown);
        using var doc = JsonDocument.Parse(missing.Json);
        Assert.Equal("missing", doc.RootElement.GetProperty("attachments")[1].GetProperty("state").GetString());

        var unknown = new ReadableExport().Export(new ReadableExportInput(
            Session(4), transcript, ["att-1", "att-2"], null));
        Assert.True(unknown.ExportIncomplete);
        Assert.Contains(unknown.IncompleteReasons, r => r.Contains("不可判定"));
        Assert.Contains("att-1：状态未知", unknown.Markdown);
    }

    [Fact]
    public void Export_JsonPreservesUtf8Chinese_AndRoundTripsText()
    {
        var transcript = string.Join('\n', Header(4), "{\"type\":\"message\",\"role\":\"user\",\"text\":\"中文安全 <tag> \\\"引号\\\"\"}") + "\n";

        var result = new ReadableExport().Export(new ReadableExportInput(Session(4), transcript));

        Assert.Contains("中文安全", result.Json);
        Assert.DoesNotContain("\\u4e2d", result.Json);
        using var doc = JsonDocument.Parse(result.Json);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        var item = doc.RootElement.GetProperty("projection").GetProperty("items")[0];
        Assert.Equal("中文安全 <tag> \"引号\"", item.GetProperty("text").GetString());
    }

    [Fact]
    public void Export_UnverifiedGeneration_MetadataOnly_MarksIncomplete()
    {
        var transcript = string.Join('\n', Header(7), FixtureFactory.MessageLine("user", "不应作为正文导出", "2026-01-05T09:01:00Z")) + "\n";

        var result = new ReadableExport().Export(new ReadableExportInput(Session(7), transcript));

        Assert.True(result.ExportIncomplete);
        Assert.Contains("未经脱敏 fixture 确证", string.Join("\n", result.IncompleteReasons));
        Assert.Contains("（无已确证的事件投影", result.Markdown);
        Assert.DoesNotContain("不应作为正文导出", result.Markdown);
        using var doc = JsonDocument.Parse(result.Json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("projection").GetProperty("items").ValueKind);
    }

    [Fact]
    public void Export_UnreadableTranscript_MarksIncomplete_AndAlwaysCarriesDisclaimer()
    {
        var result = new ReadableExport().Export(new ReadableExportInput(Session(4), null));

        Assert.True(result.ExportIncomplete);
        Assert.Contains("无法读取", string.Join("\n", result.IncompleteReasons));
        Assert.Contains("不是恢复备份", result.Markdown);
    }

    /// <summary>按标题前缀定位消息，返回围栏内原文（围栏由实现按内容加长，闭合行与开行完全相同）。</summary>
    private static string? ExtractFenced(string markdown, string headerPrefix)
    {
        var lines = markdown.Split('\n');
        var i = 0;
        while (i < lines.Length && !lines[i].StartsWith(headerPrefix, StringComparison.Ordinal))
        {
            i++;
        }

        if (i >= lines.Length)
        {
            return null;
        }

        i++;
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        if (i >= lines.Length)
        {
            return null;
        }

        var fence = lines[i];
        Assert.True(fence.Length >= 4 && fence.TrimEnd('`').Length == 0);
        var start = i + 1;
        for (var j = start; j < lines.Length; j++)
        {
            if (lines[j] == fence)
            {
                return string.Join("\n", lines[start..j]);
            }
        }

        return null;
    }
}
