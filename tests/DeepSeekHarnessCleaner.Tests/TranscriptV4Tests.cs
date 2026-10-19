using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure.Compression;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-13 V4 可验证消息预览（TC-06）。角色顺序/纯文本与 fixture 金样一致；
/// 缺字段、非文本、未知 schema 标记不完整；不展示推理/密钥；不崩溃。
/// </summary>
public sealed class TranscriptV4Tests
{
    private static readonly TranscriptProjector Projector = new();

    private static string ReadDecodedText(string path)
    {
        using var fs = File.OpenRead(path);
        var decoded = ZstdFrameDecoder.Decode(fs);
        Assert.True(decoded.IsSuccess, decoded.Error);
        return Encoding.UTF8.GetString(decoded.Content);
    }

    [Fact]
    public void V4FixtureGolden_RoleOrderAndPlainTextMatch()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV4Session();

        var result = Projector.Project(ReadDecodedText(path));

        // 金样 = WriteValidV4Session 的 fixture 确证事件序列。
        Assert.Equal(["user", "assistant"], result.Items.Select(i => i.Role));
        Assert.Equal(["synthetic v4 prompt", "synthetic v4 reply"], result.Items.Select(i => i.Text));
        Assert.Equal(
            [
                new DateTimeOffset(2026, 2, 10, 10, 1, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 10, 10, 2, 0, TimeSpan.Zero),
            ],
            result.Items.Select(i => i.At));
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void MissingContent_MarkedIncomplete()
    {
        var text = FixtureFactory.HeaderLine("s", 4, "2026-02-10T10:00:00Z") + "\n"
            + "{\"type\":\"user/message\",\"seq\":1,\"time\":1770000000000,\"data\":{\"role\":\"user\"}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
        Assert.Contains("第 2 行", result.Items[0].Text);
        Assert.Equal(1, result.IncompleteCount);
    }

    [Fact]
    public void NonTextContent_MarkedIncomplete()
    {
        var text = "{\"type\":\"user/message\",\"seq\":1,\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":\"synthetic\"}]}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
    }

    [Fact]
    public void UnconfirmedRole_MarkedIncomplete()
    {
        // tool/message 不在确证消息类型内（工具正文经 tool/result 确证）；不得猜测为可预览。
        var text = FixtureFactory.MessageLine("tool", "tool output", "2026-02-10T10:01:00Z") + "\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
        Assert.Equal(TranscriptProjector.UnknownRole, result.Items[0].Role);
    }

    [Fact]
    public void UnknownSchema_MarkedIncompleteWithSummary_NoCrash()
    {
        using var fx = FixtureRoot.Create();
        var text = ReadDecodedPlainText(fx.Factory.WriteUnknownEventSession());

        var result = Projector.Project(text);

        // TC-06：未知事件标记不完整、不崩溃；摘要含类型与行号，不猜测语义。
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.True(item.IsPartial);
        Assert.Contains("quantum_event", item.Text);
        Assert.Contains("第 2 行", item.Text);
    }

    [Fact]
    public void MalformedLine_MarkedIncomplete_NoCrash()
    {
        var text = FixtureFactory.CorruptLine + "\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
        Assert.Contains("无效 JSON", result.Items[0].Text);
    }

    [Fact]
    public void ReasoningAndSecretFields_NotDisplayed()
    {
        var text = "{\"type\":\"assistant/message\",\"seq\":1,\"data\":{\"usage\":{\"synthetic\":true},"
            + "\"api_key\":\"sk-secret\",\"message\":{\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"reasoning\",\"text\":\"hidden chain of thought\"},"
            + "{\"type\":\"text\",\"text\":\"visible answer\"}]}}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.Equal("visible answer", result.Items[0].Text);
        Assert.DoesNotContain("hidden chain of thought", result.Items[0].Text);
        Assert.DoesNotContain("sk-secret", result.Items[0].Text);
    }

    [Fact]
    public void HeaderAndAttachmentRef_SkippedFromPreview()
    {
        var text = FixtureFactory.HeaderLine("s", 4, "2026-02-10T10:00:00Z") + "\n"
            + FixtureFactory.AttachmentRefLine("att-1") + "\n"
            + FixtureFactory.MessageLine("user", "hello", "2026-02-10T10:01:00Z") + "\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.Equal("hello", result.Items[0].Text);
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void PreviewLimit_TruncatesAndDoesNotPretendComplete()
    {
        var text = string.Concat(
            Enumerable.Range(1, 5).Select(i =>
                FixtureFactory.MessageLine("user", "m" + i, "2026-02-10T10:0" + i + ":00Z") + "\n"));

        var result = Projector.Project(text, maxItems: 2);

        // design.md §34：单次预览 ≤2000 行，超限显示截断提示、不假装完整。
        Assert.Equal(2, result.Items.Count);
        Assert.True(result.Truncated);
        Assert.False(result.IsProjectionComplete);
    }

    [Fact]
    public void Cancellation_StopsProjection()
    {
        var text = string.Concat(Enumerable.Range(1, 50).Select(i =>
            FixtureFactory.MessageLine("user", "m" + i, "2026-02-10T10:00:00Z") + "\n"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Projector.Project(text, cancellationToken: cts.Token));
    }

    [Fact]
    public void PlainV4Session_PreviewMatchesZstdProjection()
    {
        using var fx = FixtureRoot.Create();
        var plain = fx.Factory.WriteSessionFile("proj-alpha", "synth-v4p-001", "session.v4.jsonl",
            FixtureFactory.Utf8(
                FixtureFactory.HeaderLine("synth-v4p-001", 4, "2026-02-10T10:00:00Z") + "\n"
                + FixtureFactory.MessageLine("user", "synthetic v4 prompt", "2026-02-10T10:01:00Z") + "\n"
                + FixtureFactory.MessageLine("assistant", "synthetic v4 reply", "2026-02-10T10:02:00Z") + "\n"));

        var result = Projector.Project(File.ReadAllText(plain, Encoding.UTF8));

        Assert.Equal(["user", "assistant"], result.Items.Select(i => i.Role));
        Assert.Equal(["synthetic v4 prompt", "synthetic v4 reply"], result.Items.Select(i => i.Text));
        Assert.True(result.IsProjectionComplete);
    }

    // ---- M-03 真实信封映射用例（M-01 证据 DIAG-event-format.txt） ----

    [Fact]
    public void MultiTextBlocks_ConcatenatedWithNewline()
    {
        // assistant/message 正文嵌套在 data.message（M-03 msg-probe 确证）。
        var text = "{\"type\":\"assistant/message\",\"seq\":1,\"data\":{\"message\":{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"part one\"},{\"type\":\"image\",\"source\":\"synthetic\"},"
            + "{\"type\":\"text\",\"text\":\"part two\"}]}}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.Equal("assistant", result.Items[0].Role);
        Assert.Equal("part one\npart two", result.Items[0].Text);
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void AssistantMessage_TextOnlyBlocks_Projected()
    {
        // 真实 assistant/message 多数仅含 tool-call/reasoning 块：无文本块按不完整处理，不猜测正文。
        var text = "{\"type\":\"assistant/message\",\"seq\":1,\"data\":{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool-call\",\"name\":\"synthetic\"}]}}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
    }

    [Fact]
    public void SystemAndDeveloperMessages_Projected()
    {
        var text = "{\"type\":\"system/message\",\"seq\":1,\"data\":{\"message\":{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"sys note\"}]}}}\n"
            + "{\"type\":\"developer/message\",\"seq\":2,\"data\":{\"message\":{\"role\":\"developer\",\"content\":[{\"type\":\"text\",\"text\":\"dev note\"}]}}}\n";

        var result = Projector.Project(text);

        Assert.Equal(["system", "developer"], result.Items.Select(i => i.Role));
        Assert.Equal(["sys note", "dev note"], result.Items.Select(i => i.Text));
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void ToolResult_ProjectedWithToolRole()
    {
        var text = "{\"type\":\"tool/result\",\"seq\":1,\"time\":1770000060000,\"data\":{\"message\":"
            + "{\"role\":\"tool\",\"content\":[{\"type\":\"text\",\"text\":\"tool output\"}]}}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.Equal("tool", result.Items[0].Role);
        Assert.Equal("tool output", result.Items[0].Text);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1770000060000),
            result.Items[0].At);
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void ToolResult_MissingMessage_MarkedIncomplete()
    {
        var text = "{\"type\":\"tool/result\",\"seq\":1,\"data\":{\"toolName\":\"synthetic\"}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
    }

    [Fact]
    public void AggregateChunkLines_SkippedNotIncomplete()
    {
        // 聚合 chunk 行（seq0/time0 + data.dt[]/data.texts[]）为流式片段：预览跳过、不重复投影、不标不完整。
        var text = "{\"type\":\"reasoning-chunks\",\"seq0\":1,\"time0\":1770000000000,\"data\":{\"dt\":[1,2],\"texts\":[\"a\",\"b\"]}}\n"
            + "{\"type\":\"tool-call-chunks\",\"seq0\":3,\"time0\":1770000001000,\"data\":{\"dt\":[1],\"texts\":[\"c\"]}}\n"
            + "{\"type\":\"text-chunks\",\"seq0\":4,\"time0\":1770000002000,\"data\":{\"dt\":[1],\"texts\":[\"d\"]}}\n";

        var result = Projector.Project(text);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.IncompleteCount);
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void EnvelopeTime_EpochMillisParsed_MissingTimeNullAt()
    {
        var text = "{\"type\":\"user/message\",\"seq\":1,\"time\":1770000060000,\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"with time\"}]}}\n"
            + "{\"type\":\"user/message\",\"seq\":2,\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"no time\"}]}}\n";

        var result = Projector.Project(text);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1770000060000), result.Items[0].At);
        Assert.Null(result.Items[1].At);
        Assert.True(result.IsProjectionComplete);
    }

    [Fact]
    public void KnownNonMessageTypes_MarkedIncompleteWithTypeAndLine()
    {
        // 词汇表内但未投影的类型（如 step/start）仍按类型/行号 + 不完整摘要输出，不猜测语义。
        var text = "{\"type\":\"step/start\",\"seq\":1,\"time\":1770000000000,\"data\":{\"synthetic\":true}}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
        Assert.Contains("step/start", result.Items[0].Text);
        Assert.Contains("第 1 行", result.Items[0].Text);
    }

    private static string ReadDecodedPlainText(string path) => File.ReadAllText(path, Encoding.UTF8);
}
