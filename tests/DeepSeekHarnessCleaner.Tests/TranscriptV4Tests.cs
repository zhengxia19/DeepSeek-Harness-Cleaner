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
    public void MissingTextField_MarkedIncomplete()
    {
        var text = FixtureFactory.HeaderLine("s", 4, "2026-02-10T10:00:00Z") + "\n"
            + "{\"type\":\"message\",\"role\":\"user\",\"ts\":\"2026-02-10T10:01:00Z\"}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
        Assert.Contains("第 2 行", result.Items[0].Text);
        Assert.Equal(1, result.IncompleteCount);
    }

    [Fact]
    public void NonTextContent_MarkedIncomplete()
    {
        var text = "{\"type\":\"message\",\"role\":\"user\",\"text\":42}\n";

        var result = Projector.Project(text);

        Assert.Single(result.Items);
        Assert.True(result.Items[0].IsPartial);
    }

    [Fact]
    public void UnconfirmedRole_MarkedIncomplete()
    {
        // fixture 仅确证 user/assistant；tool 角色不得猜测为可预览。
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
        var text = "{\"type\":\"message\",\"role\":\"assistant\",\"text\":\"visible answer\","
            + "\"reasoning\":\"hidden chain of thought\",\"api_key\":\"sk-secret\"}\n";

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

    private static string ReadDecodedPlainText(string path) => File.ReadAllText(path, Encoding.UTF8);
}
