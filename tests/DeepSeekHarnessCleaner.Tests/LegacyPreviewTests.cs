using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-14 旧版本预览安全退化（TC-06/TC-20）。旧版未知结构显示“不完整/仅元数据”；
/// 无法解析时仍支持原始备份；不存在虚构的消息；最高代际失败不隐式回退。
/// </summary>
public sealed class LegacyPreviewTests
{
    private static readonly LegacyPreview Preview = new();
    private static readonly DateTimeOffset Epoch = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static PhysicalLog Log(int generation, ParseStatus status = ParseStatus.Valid, long bytes = 100) =>
        new($@"X:\synth\session.v{generation}.jsonl", generation, Compressed: false, bytes, Epoch, null, status);

    private static SessionSummary Summary(ParseStatus status, params PhysicalLog[] logs) =>
        new("synth-legacy-001", "proj-alpha", Epoch, null, DateOrigin.Header, logs, status)
        {
            TotalBytes = logs.Sum(l => l.Bytes),
        };

    [Fact]
    public void SelectPreviewGeneration_PicksNumericallyHighest()
    {
        var summary = Summary(ParseStatus.Valid, Log(0), Log(4), Log(2));
        Assert.Equal(4, LegacyPreview.SelectPreviewGeneration(summary));
        Assert.Null(LegacyPreview.SelectPreviewGeneration(Summary(ParseStatus.Corrupt)));
    }

    [Fact]
    public void V0FixtureSession_ProjectsVerifiedMessages_NoFabrication()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV0Session();
        var summary = Summary(ParseStatus.Valid, Log(0));

        var result = Preview.Build(summary, File.ReadAllText(path, Encoding.UTF8));

        // v0 消息 schema 经脱敏 fixture 确证：金样投影，不虚构。
        Assert.Equal(LegacyPreviewMode.Projected, result.Mode);
        Assert.Equal(0, result.PreviewGeneration);
        Assert.NotNull(result.Projection);
        Assert.Single(result.Projection!.Items);
        Assert.Equal("user", result.Projection.Items[0].Role);
        Assert.Equal("synthetic v0 prompt", result.Projection.Items[0].Text);
        Assert.True(result.Projection.IsProjectionComplete);
        Assert.True(result.SupportsRawBackup);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UnverifiedGeneration_MetadataOnly_NoFabricatedMessages(int generation)
    {
        var transcript = FixtureFactory.HeaderLine("synth-legacy-001", generation, "2026-01-05T09:00:00Z") + "\n"
            + FixtureFactory.MessageLine("user", "maybe a message?", "2026-01-05T09:01:00Z") + "\n";
        var summary = Summary(ParseStatus.Valid, Log(generation));

        var result = Preview.Build(summary, transcript);

        // v1-v3 schema 未经 fixture 确证：仅元数据，绝不输出猜测的事件正文。
        Assert.Equal(LegacyPreviewMode.MetadataOnly, result.Mode);
        Assert.Equal(generation, result.PreviewGeneration);
        Assert.Null(result.Projection);
        Assert.Contains(result.MetadataLines, line => line.Contains($"代际 {generation}"));
        Assert.DoesNotContain("maybe a message?", string.Join("\n", result.MetadataLines));
        Assert.True(result.SupportsRawBackup);
    }

    [Fact]
    public void HighestGenerationUnreadable_NoFallbackToOlder()
    {
        // 同一会话 v0 可读 + v4（最高代际）损坏：不得回退 v0 假装当前内容。
        var summary = Summary(ParseStatus.Partial, Log(0), Log(4, ParseStatus.Corrupt));

        var result = Preview.Build(summary, previewGenerationTranscript: null);

        Assert.Equal(LegacyPreviewMode.MetadataOnly, result.Mode);
        Assert.Equal(4, result.PreviewGeneration);
        Assert.Null(result.Projection);
        Assert.Contains("4", result.Reason);
        Assert.Contains("不回退", result.Reason);
        Assert.True(result.SupportsRawBackup);
    }

    [Fact]
    public void V0UnknownStructure_MarkedIncomplete_NotGuessed()
    {
        var transcript = FixtureFactory.HeaderLine("synth-legacy-001", 0, "2026-01-05T09:00:00Z") + "\n"
            + FixtureFactory.UnknownEventLine + "\n";
        var summary = Summary(ParseStatus.Partial, Log(0));

        var result = Preview.Build(summary, transcript);

        // TC-06：旧版未知事件标记不完整、不崩溃；摘要含类型与行号。
        Assert.Equal(LegacyPreviewMode.Projected, result.Mode);
        Assert.Single(result.Projection!.Items);
        var item = result.Projection.Items[0];
        Assert.True(item.IsPartial);
        Assert.Contains("quantum_event", item.Text);
        Assert.Contains("第 2 行", item.Text);
        Assert.False(result.Projection.IsProjectionComplete);
    }

    [Fact]
    public void CorruptTranscript_MarkedIncomplete_StillSupportsRawBackup()
    {
        var transcript = FixtureFactory.CorruptLine + "\n";
        var summary = Summary(ParseStatus.Corrupt, Log(0, ParseStatus.Corrupt));

        var result = Preview.Build(summary, transcript);

        // TC-20：坏数据不卡死、不误判不可备份；原始文件备份不受预览失败影响。
        Assert.Equal(LegacyPreviewMode.Projected, result.Mode);
        Assert.True(result.SupportsRawBackup);
        Assert.Single(result.Projection!.Items);
        Assert.True(result.Projection.Items[0].IsPartial);
        Assert.Contains("无效 JSON", result.Projection.Items[0].Text);
    }

    [Fact]
    public void EmptyTranscript_MetadataOnly_NoCrash()
    {
        var summary = Summary(ParseStatus.Corrupt, Log(0, ParseStatus.Corrupt));

        var result = Preview.Build(summary, string.Empty);

        // TC-20：空数据不崩溃；显示异常而非虚构内容。
        Assert.Equal(LegacyPreviewMode.MetadataOnly, result.Mode);
        Assert.Null(result.Projection);
        Assert.True(result.SupportsRawBackup);
    }

    [Fact]
    public void NoGenerations_MetadataOnly_ReasonGiven()
    {
        var result = Preview.Build(Summary(ParseStatus.Unreadable), previewGenerationTranscript: null);

        Assert.Equal(LegacyPreviewMode.MetadataOnly, result.Mode);
        Assert.Null(result.PreviewGeneration);
        Assert.NotEmpty(result.Reason);
        Assert.True(result.SupportsRawBackup);
    }

    [Fact]
    public void MetadataLines_AreConservativeSummary()
    {
        var summary = Summary(ParseStatus.Valid, Log(0, bytes: 120), Log(2, bytes: 30));

        var result = Preview.Build(summary, previewGenerationTranscript: null);

        Assert.Contains("会话：synth-legacy-001", result.MetadataLines);
        Assert.Contains("项目：proj-alpha", result.MetadataLines);
        Assert.Contains(result.MetadataLines, l => l.Contains("共 150 字节"));
        Assert.Contains("预览代际：2（最高规范代际）", result.MetadataLines);
    }
}
