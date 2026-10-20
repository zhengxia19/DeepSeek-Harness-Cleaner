using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>单次会话预览结果：携带请求时的会话对象（UI 侧防串会话核对）、LegacyPreview 结果与附加警告。</summary>
public sealed record PreviewOutcome(
    SessionSummary Session,
    LegacyPreviewResult Result,
    IReadOnlyList<string> Warnings);

/// <summary>
/// T-19 会话预览演示器：对预览代际做路径白名单复验（PathGuard），受限读取/解压
/// （单行与总量上限，CompressionLimits），再经 LegacyPreview 构建安全预览——
/// fixture 确证代际（v0/v4）逐事件投影，未知代际仅元数据提示，不可读不空白。
/// 在后台线程执行（Task.Run），不依赖任何 WinForms 类型。
/// </summary>
public sealed class PreviewPresenter
{
    private readonly PathGuard _pathGuard = new();
    private readonly LegacyPreview _legacy = new();

    public Task<PreviewOutcome> PreviewAsync(string sessionRoot, SessionSummary session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionRoot);
        ArgumentNullException.ThrowIfNull(session);
        return Task.Run(() => Build(sessionRoot, session, ct), ct);
    }

    private PreviewOutcome Build(string sessionRoot, SessionSummary session, CancellationToken ct)
    {
        var warnings = new List<string>();
        var selected = LegacyPreview.SelectPreviewGeneration(session);
        string? transcript = null;

        var log = selected is null
            ? null
            : session.Generations.FirstOrDefault(g => g.Generation == selected!.Value);

        if (log is not null)
        {
            var check = _pathGuard.Check(sessionRoot, log.FullPath, allowDirectory: false);
            if (!check.IsAllowed)
            {
                warnings.Add($"预览文件未通过路径白名单：{check.ErrorCode}");
            }
            else
            {
                transcript = ReadTranscript(check.FullPath, log.Compressed, warnings, ct);
            }
        }

        return new PreviewOutcome(session, _legacy.Build(session, transcript, ct), warnings);
    }

    private static string? ReadTranscript(string fullPath, bool compressed, List<string> warnings, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (compressed)
            {
                var decoded = ZstdFrameDecoder.Decode(stream, CompressionLimits.MaxSessionDecompressedBytes);
                switch (decoded.Status)
                {
                    case ZstdDecodeStatus.Success:
                        return Encoding.UTF8.GetString(decoded.Content);
                    case ZstdDecodeStatus.EmptyInput:
                        warnings.Add("预览文件解压后为空。");
                        return string.Empty;
                    case ZstdDecodeStatus.TruncatedTail:
                        warnings.Add("zstd 尾帧截断，预览基于可读部分（不完整）。");
                        return Encoding.UTF8.GetString(decoded.Content);
                    case ZstdDecodeStatus.DecompressedLimitExceeded:
                        warnings.Add("解压总量超限，不提供预览正文。");
                        return null;
                    default:
                        warnings.Add("zstd 解压失败：" + (decoded.Error ?? decoded.Status.ToString()));
                        return null;
                }
            }

            // 明文：读取上限 + 1 字节判定是否超限，超限不提供正文（与事件流同一上限）。
            var limit = CompressionLimits.MaxSessionDecompressedBytes;
            var buffer = new byte[checked((int)limit) + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
                ct.ThrowIfCancellationRequested();
            }

            if (total > limit)
            {
                warnings.Add("预览文件超过大小上限，不提供预览正文。");
                return null;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (IOException)
        {
            warnings.Add("预览文件不可读（IO）。");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add("预览文件不可读（权限）。");
            return null;
        }
    }

    /// <summary>渲染为纯文本预览（design.md §4：UI 不渲染 Markdown/HTML；不完整事件显式标记）。</summary>
    public static string Render(PreviewOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("—— 会话预览 ——");
        foreach (var line in outcome.Result.MetadataLines)
        {
            sb.AppendLine(line);
        }

        sb.AppendLine($"预览模式：{outcome.Result.Mode}");
        sb.AppendLine($"说明：{outcome.Result.Reason}");
        sb.AppendLine($"支持原始备份：{(outcome.Result.SupportsRawBackup ? "是" : "否")}");
        foreach (var warning in outcome.Warnings)
        {
            sb.AppendLine("警告：" + warning);
        }

        sb.AppendLine("—— 事件正文 ——");
        if (outcome.Result.Projection is { } projection)
        {
            foreach (var item in projection.Items)
            {
                if (sb.Length >= 240_000)
                {
                    sb.AppendLine("（预览显示达到 240,000 字符上限；可通过原始备份保留全部内容）");
                    break;
                }
                var at = item.At is null
                    ? string.Empty
                    : $" @ {item.At.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                var limited = item.Text.Length > 16_000
                    ? item.Text[..16_000] + "（当前消息仅显示前 16,000 字符）"
                    : item.Text;
                sb.AppendLine($"[{item.Role}]{at} {limited}");
            }

            if (projection.Truncated)
            {
                sb.AppendLine($"（达到预览上限 {TranscriptProjector.MaxPreviewItems} 条，已截断）");
            }

            if (projection.IncompleteCount > 0)
            {
                sb.AppendLine($"（含 {projection.IncompleteCount} 个不完整事件标记）");
            }
        }
        else
        {
            sb.AppendLine("（无事件正文：见上方说明，不猜测未确证 schema 的内容）");
        }

        return sb.ToString();
    }
}
