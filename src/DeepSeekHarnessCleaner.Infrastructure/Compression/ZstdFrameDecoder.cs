using ZstdSharp;

namespace DeepSeekHarnessCleaner.Infrastructure.Compression;

public enum ZstdDecodeStatus
{
    Success,
    EmptyInput,
    TruncatedTail,
    CorruptData,
    DecompressedLimitExceeded,
}

/// <summary>物理解压结果；仅 Success 表示可当作正常读取。</summary>
public sealed record ZstdDecodeResult(ZstdDecodeStatus Status, byte[] Content, string? Error = null)
{
    public bool IsSuccess => Status == ZstdDecodeStatus.Success;
}

/// <summary>
/// 可复用的 Zstd 多帧流式解压适配器（design.md §2/§3、TC-04、TC-20）。
/// 实测 ZstdSharp 0.8.8 行为：连接帧由 DecompressionStream 顺序解出；截断尾帧抛
/// EndOfStreamException；带 checksum 的帧损坏抛 ZstdException(checksum_wrong)；
/// 默认 Wrap 不写 content checksum，因此本项目写入端必须显式启用 checksumFlag。
/// </summary>
public static class ZstdFrameDecoder
{
    private const int BufferBytes = 64 * 1024;

    /// <summary>压缩为单个带内容 checksum 的 Zstd frame（本项目写入端契约）。</summary>
    public static byte[] CompressFrame(ReadOnlySpan<byte> data, int level = 3)
    {
        using var compressor = new Compressor(level);
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>
    /// 流式解压连接帧序列。截断尾帧与损坏帧分别返回 TruncatedTail/CorruptData，
    /// 绝不返回 Success；解压量超过 <paramref name="maxTotalBytes"/> 立即中止。
    /// </summary>
    public static ZstdDecodeResult Decode(Stream input, long maxTotalBytes = CompressionLimits.MaxSessionDecompressedBytes,
        bool retainPrefixOnLimit = false, CancellationToken cancellationToken = default)
    {
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));

        if (input.CanSeek && input.Length - input.Position == 0)
            return new ZstdDecodeResult(ZstdDecodeStatus.EmptyInput, Array.Empty<byte>());

        using var output = new MemoryStream();
        var buffer = new byte[BufferBytes];
        long total = 0;
        try
        {
            using var decompression = new DecompressionStream(input, BufferBytes, checkEndOfStream: true, leaveOpen: true);
            int read;
            while ((read = decompression.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > maxTotalBytes)
                {
                    // Opt-in for read-only preview only. Status still refuses full decoding;
                    // all validation/cleanup callers retain the default empty-content result.
                    if (retainPrefixOnLimit)
                        output.Write(buffer, 0, checked((int)(maxTotalBytes - output.Length)));
                    return new ZstdDecodeResult(
                        ZstdDecodeStatus.DecompressedLimitExceeded,
                        retainPrefixOnLimit ? output.ToArray() : Array.Empty<byte>(),
                        $"解压量超过上限 {maxTotalBytes} 字节。");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (EndOfStreamException ex)
        {
            return new ZstdDecodeResult(ZstdDecodeStatus.TruncatedTail, Array.Empty<byte>(), "尾帧截断：" + ex.Message);
        }
        catch (ZstdException ex)
        {
            return new ZstdDecodeResult(ZstdDecodeStatus.CorruptData, Array.Empty<byte>(), "Zstd 数据损坏：" + ex.Code);
        }

        return new ZstdDecodeResult(ZstdDecodeStatus.Success, output.ToArray());
    }
}
