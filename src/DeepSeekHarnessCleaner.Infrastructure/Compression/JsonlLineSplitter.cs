namespace DeepSeekHarnessCleaner.Infrastructure.Compression;

/// <summary>
/// JSONL 物理行切分（design.md §33）：单行超过 <see cref="CompressionLimits.MaxLineBytes"/>
/// 时计数并丢弃该行正文（显示截断提示，不记录消息正文），其余行照常返回。
/// 行按 LF 切分；不裁剪 CR，由上层语义解析处理。
/// </summary>
public sealed record LineSplitResult(IReadOnlyList<byte[]> Lines, int TooLongLineCount)
{
    public bool HasTruncation => TooLongLineCount > 0;
}

public static class JsonlLineSplitter
{
    public static LineSplitResult Split(ReadOnlySpan<byte> content, int maxLineBytes = CompressionLimits.MaxLineBytes)
    {
        if (maxLineBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes));

        var lines = new List<byte[]>();
        int tooLong = 0;
        int start = 0;

        void AddLine(ReadOnlySpan<byte> line)
        {
            if (line.Length > maxLineBytes)
            {
                tooLong++;
                return;
            }

            lines.Add(line.ToArray());
        }

        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != (byte)'\n')
                continue;

            AddLine(content[start..i]);
            start = i + 1;
        }

        if (start < content.Length)
            AddLine(content[start..]);

        return new LineSplitResult(lines, tooLong);
    }
}
