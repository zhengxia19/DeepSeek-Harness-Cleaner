namespace DeepSeekHarnessCleaner.Infrastructure.Compression;

/// <summary>会话日志物理读取的硬性上限（design.md §33-34）。</summary>
public static class CompressionLimits
{
    /// <summary>单行 JSONL 最大字节数（8 MiB）。超限显示截断提示，不记录消息正文。</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>单个会话解压后总量最大字节数（256 MiB）。</summary>
    public const long MaxSessionDecompressedBytes = 256L * 1024 * 1024;
}
