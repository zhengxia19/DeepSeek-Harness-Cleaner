namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// 字节限额读取的单一实现（B04）："填充到 count 即停"与"读到 limit 即停/超限判定"。
/// 各类数值上限（行上限、快照上限等）由调用方决定并保留各自的错误码/返回形态；本类不含策略。
/// 先例：SessionHeaderReader 的 maxLineBytes+1 缓冲判定（ReadAtMost 原始实现）。
/// </summary>
internal static class BoundedRead
{
    /// <summary>至多读取 count 字节填充 buffer，返回实际读取数（EOF 提前返回）。调用方以 count=limit+1 判定超限。</summary>
    internal static int ReadAtMost(Stream stream, byte[] buffer, int count, CancellationToken ct = default)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                break;
            total += read;
            ct.ThrowIfCancellationRequested();
        }

        return total;
    }

    /// <summary>分块读取流直至 EOF 或再读一块将超过 limit；exceeded=true 表示在 limit 处截断，返回内容为已读前缀。</summary>
    internal static byte[] ReadAllUpTo(Stream stream, long limit, out bool exceeded, CancellationToken ct = default)
    {
        exceeded = false;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + count > limit)
            {
                exceeded = true;
                break;
            }

            buffer.Write(chunk, 0, count);
            ct.ThrowIfCancellationRequested();
        }

        return buffer.ToArray();
    }
}
