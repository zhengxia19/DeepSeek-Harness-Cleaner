using System.Globalization;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>旧版预览退化模式（T-14）。</summary>
public enum LegacyPreviewMode
{
    /// <summary>事件 schema 经 fixture 确证，提供逐事件投影。</summary>
    Projected,

    /// <summary>仅提供元数据与保守摘要；不提供事件正文（不完整）。</summary>
    MetadataOnly,
}

/// <summary>
/// 旧版会话预览结果。SupportsRawBackup 恒为 true：预览失败不阻断原始文件备份
/// （design.md §35 物理解码与安全只读元数据为最低保证）。
/// </summary>
public sealed record LegacyPreviewResult(
    LegacyPreviewMode Mode,
    int? PreviewGeneration,
    IReadOnlyList<string> MetadataLines,
    TranscriptProjection? Projection,
    bool SupportsRawBackup,
    string Reason);

/// <summary>
/// v0-v3 旧版预览安全退化（T-14）。为旧代际提供元数据和已验证类型的保守摘要；
/// 不实现完整官方迁移；最高代际无法读取时显示异常，不回退旧代际假装当前内容（design.md §31/§35）。
/// </summary>
public sealed class LegacyPreview
{
    /// <summary>
    /// 事件 schema 经 Harness 0.2.0.0 脱敏 fixture 确证的代际（v0 与 v4）。
    /// v1-v3 及未来未知代际未确证：仅元数据，不猜测事件正文，杜绝虚构消息。
    /// </summary>
    private static readonly int[] FixtureVerifiedGenerations = [0, 4];

    private static readonly TranscriptProjector Projector = new();

    /// <summary>
    /// 预览代际 = numerically highest canonical generation（design.md §31）。
    /// 调用方据此解码对应文件并把文本传给 <see cref="Build"/>；无候选返回 null。
    /// </summary>
    public static int? SelectPreviewGeneration(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        int? highest = null;
        foreach (var log in summary.Generations)
        {
            if (highest is null || log.Generation > highest.Value)
            {
                highest = log.Generation;
            }
        }

        return highest;
    }

    /// <summary>
    /// 构建旧版预览。<paramref name="previewGenerationTranscript"/> 必须是
    /// <see cref="SelectPreviewGeneration"/> 选定代际的已解码文本；不可读时传 null。
    /// 绝不使用更低代际的文本替代（不隐式回退）。
    /// </summary>
    public LegacyPreviewResult Build(SessionSummary summary, string? previewGenerationTranscript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var metadata = BuildMetadataLines(summary);
        var selected = SelectPreviewGeneration(summary);
        if (selected is null)
        {
            return new LegacyPreviewResult(
                LegacyPreviewMode.MetadataOnly, null, metadata, null,
                SupportsRawBackup: true, "会话没有可预览的物理日志文件。");
        }

        if (string.IsNullOrEmpty(previewGenerationTranscript))
        {
            // 最高代际不可读/为空：显示异常，不回退旧代际假装当前内容。
            return new LegacyPreviewResult(
                LegacyPreviewMode.MetadataOnly, selected, metadata, null,
                SupportsRawBackup: true,
                $"最高代际 {selected.Value} 无法读取或内容为空；不回退到更低代际，原始文件仍可备份。");
        }

        if (!FixtureVerifiedGenerations.Contains(selected.Value))
        {
            return new LegacyPreviewResult(
                LegacyPreviewMode.MetadataOnly, selected, metadata, null,
                SupportsRawBackup: true,
                $"代际 {selected.Value} 的事件 schema 未经脱敏 fixture 确证；仅提供元数据摘要（不完整），不猜测事件正文。");
        }

        var projection = Projector.Project(previewGenerationTranscript, cancellationToken: cancellationToken);
        return new LegacyPreviewResult(
            LegacyPreviewMode.Projected, selected, metadata, projection,
            SupportsRawBackup: true,
            projection.IsProjectionComplete
                ? "事件 schema 经 fixture 确证，投影完整。"
                : $"投影含 {projection.IncompleteCount} 个不完整事件标记"
                  + (projection.Truncated ? "，且达到预览上限被截断" : string.Empty) + "。");
    }

    private static IReadOnlyList<string> BuildMetadataLines(SessionSummary summary)
    {
        var lines = new List<string>
        {
            $"会话：{summary.SessionId ?? "(未知)"}",
            $"项目：{summary.Project ?? "(未知)"}",
            $"创建时间：{FormatDate(summary.CreatedAt)}（来源 {summary.DateOrigin}）",
            $"库存状态：{summary.Status}",
            $"物理日志代际：{summary.Generations.Count} 个，共 {summary.TotalBytes} 字节",
        };

        foreach (var log in summary.Generations)
        {
            lines.Add(
                $"  代际 {log.Generation}：{(log.Compressed ? "zstd" : "明文")}，{log.Bytes} 字节，" +
                $"修改于 {FormatDate(log.ModifiedAt)}，状态 {log.Status}");
        }

        var selected = SelectPreviewGeneration(summary);
        lines.Add($"预览代际：{(selected is null ? "(无)" : selected.Value.ToString(CultureInfo.InvariantCulture))}（最高规范代际）");
        return lines;
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value is null
            ? "(未知)"
            : value.Value.ToString("O", CultureInfo.InvariantCulture);
}
