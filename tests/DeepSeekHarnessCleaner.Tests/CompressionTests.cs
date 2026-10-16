using System.Text;
using DeepSeekHarnessCleaner.Infrastructure.Compression;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;
using ZstdSharp;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-05：Zstd 多帧物理解码验收（TC-04、TC-20）。
/// 覆盖：连接帧字节级一致、截断尾帧、checksum 损坏、空帧/空输入、
/// 8MiB 行与 256MiB 解压量保护。
/// </summary>
public sealed class CompressionTests
{
    private static byte[] ConcatBytes(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts)
            ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private static byte[] CompressFrames(params string[] plainFrames) =>
        ConcatBytes(plainFrames.Select(f => ZstdFrameDecoder.CompressFrame(FixtureFactory.Utf8(f))).ToArray());

    // ---- 连接帧：输出字节逐个一致 ----

    [Fact]
    public void MultiFrame_DecodesByteIdentical()
    {
        var frames = new[] { "line one\n", "line two\nline three\n", "", "line four\n" };
        var compressed = CompressFrames(frames);
        var expected = ConcatBytes(frames.Select(f => FixtureFactory.Utf8(f)).ToArray());

        var result = ZstdFrameDecoder.Decode(new MemoryStream(compressed));

        Assert.Equal(ZstdDecodeStatus.Success, result.Status);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void MultiFrameFile_DecodesThenSplitsToLines()
    {
        using var fixture = FixtureRoot.Create();
        var path = Path.Combine(fixture.Root, "session.v4.jsonl.zstd");
        File.WriteAllBytes(path, CompressFrames("a\nb\n", "c\n"));

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoded = ZstdFrameDecoder.Decode(fs);
        var split = JsonlLineSplitter.Split(decoded.Content);

        Assert.True(decoded.IsSuccess);
        Assert.Equal(3, split.Lines.Count);
        Assert.Equal("a", Encoding.UTF8.GetString(split.Lines[0]));
        Assert.Equal("b", Encoding.UTF8.GetString(split.Lines[1]));
        Assert.Equal("c", Encoding.UTF8.GetString(split.Lines[2]));
        Assert.False(split.HasTruncation);
    }

    // ---- 截断尾帧：不可当成正常成功 ----

    [Fact]
    public void TruncatedTailFrame_NotSuccess()
    {
        var compressed = CompressFrames("frame one\n", "frame two\n");
        var truncated = compressed[..^8];

        var result = ZstdFrameDecoder.Decode(new MemoryStream(truncated));

        Assert.Equal(ZstdDecodeStatus.TruncatedTail, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TruncatedMidFrame_NotSuccess()
    {
        var compressed = CompressFrames(string.Concat(Enumerable.Repeat("payload ", 5000)));
        var truncated = compressed[..(compressed.Length / 2)];

        var result = ZstdFrameDecoder.Decode(new MemoryStream(truncated));

        Assert.False(result.IsSuccess);
        Assert.True(result.Status is ZstdDecodeStatus.TruncatedTail or ZstdDecodeStatus.CorruptData);
    }

    [Fact]
    public void FixtureTruncatedSession_DecoderRejects()
    {
        using var fixture = FixtureRoot.Create();
        var path = fixture.Factory.WriteTruncatedZstdSession("projA", "sess-trunc");

        ZstdDecodeResult result;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = ZstdFrameDecoder.Decode(fs);

        Assert.False(result.IsSuccess);
        Assert.True(result.Status is ZstdDecodeStatus.TruncatedTail or ZstdDecodeStatus.CorruptData);
    }

    // ---- 损坏：checksum 必须捕获 ----

    [Fact]
    public void CorruptedFrame_WithChecksum_CorruptData()
    {
        var compressed = CompressFrames(string.Concat(Enumerable.Repeat("The quick brown fox. ", 400)));
        compressed[compressed.Length / 2] ^= 0xFF;

        var result = ZstdFrameDecoder.Decode(new MemoryStream(compressed));

        Assert.Equal(ZstdDecodeStatus.CorruptData, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TrailingGarbageAfterFrames_NotSuccess()
    {
        var compressed = ConcatBytes(CompressFrames("ok\n"), new byte[] { 0x00, 0x41, 0x42, 0x43 });

        var result = ZstdDecodeResultOf(compressed);

        Assert.False(result.IsSuccess);
    }

    private static ZstdDecodeResult ZstdDecodeResultOf(byte[] input) =>
        ZstdFrameDecoder.Decode(new MemoryStream(input));

    [Fact]
    public void WriterChecksumContract_DefaultWrapSilentlyCorrupts_ZstdSharpDefaultHasNoChecksum()
    {
        // 实测：ZstdSharp 默认 Wrap 不写 content checksum，中部损坏会静默产出错误正文；
        // 因此本项目写入端（CompressFrame）必须启用 checksumFlag。
        var plain = FixtureFactory.Utf8(string.Concat(Enumerable.Repeat("The quick brown fox. ", 400)));
        byte[] noChecksum;
        using (var compressor = new Compressor(3))
            noChecksum = compressor.Wrap(plain).ToArray();
        noChecksum[noChecksum.Length / 2] ^= 0xFF;

        var silent = ZstdFrameDecoder.Decode(new MemoryStream(noChecksum));

        Assert.True(silent.IsSuccess);
        Assert.False(silent.Content.AsSpan().SequenceEqual(plain));
    }

    // ---- 空帧与空输入 ----

    [Fact]
    public void EmptyFrame_DecodesSuccessWithZeroBytes()
    {
        var compressed = ZstdFrameDecoder.CompressFrame(Array.Empty<byte>());

        var result = ZstdFrameDecoder.Decode(new MemoryStream(compressed));

        Assert.Equal(ZstdDecodeStatus.Success, result.Status);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void EmptyInputFile_DistinguishedAsEmptyInput()
    {
        var result = ZstdFrameDecoder.Decode(new MemoryStream());

        Assert.Equal(ZstdDecodeStatus.EmptyInput, result.Status);
        Assert.Empty(result.Content);
    }

    // ---- 256MiB 解压量保护 ----

    [Fact]
    public void DecompressedVolume_ExceedsCustomLimit_Triggered()
    {
        var compressed = CompressFrames("hello world, this payload is longer than ten bytes\n");

        var result = ZstdFrameDecoder.Decode(new MemoryStream(compressed), maxTotalBytes: 10);

        Assert.Equal(ZstdDecodeStatus.DecompressedLimitExceeded, result.Status);
    }

    [Fact]
    public void DecompressedVolume_ExactlyAtLimit_Succeeds()
    {
        var frame = "exactly-at-limit\n";
        var compressed = CompressFrames(frame);
        long size = FixtureFactory.Utf8(frame).LongLength;

        var result = ZstdFrameDecoder.Decode(new MemoryStream(compressed), maxTotalBytes: size);

        Assert.Equal(ZstdDecodeStatus.Success, result.Status);
    }

    [Fact]
    public void DecompressedVolume_ExceedsDefault256MiB_Triggered()
    {
        // 65 × 4MiB 零字节帧 = 260MiB 解压量 > 256MiB 上限。
        var zeroFrame = new byte[4 * 1024 * 1024];
        using var ms = new MemoryStream();
        for (int i = 0; i < 65; i++)
        {
            var f = ZstdFrameDecoder.CompressFrame(zeroFrame);
            ms.Write(f, 0, f.Length);
        }

        var result = ZstdFrameDecoder.Decode(new MemoryStream(ms.ToArray()));

        Assert.Equal(ZstdDecodeStatus.DecompressedLimitExceeded, result.Status);
    }

    // ---- 8MiB 行保护 ----

    [Fact]
    public void Line_Exceeding8MiB_CountedAndBodyDropped()
    {
        var longLine = new string('a', CompressionLimits.MaxLineBytes + 1);
        var content = FixtureFactory.Utf8("first\n" + longLine + "\nlast\n");

        var split = JsonlLineSplitter.Split(content);

        Assert.Equal(1, split.TooLongLineCount);
        Assert.True(split.HasTruncation);
        Assert.Equal(2, split.Lines.Count);
        Assert.Equal("first", Encoding.UTF8.GetString(split.Lines[0]));
        Assert.Equal("last", Encoding.UTF8.GetString(split.Lines[1]));
    }

    [Fact]
    public void Line_Exactly8MiB_Preserved()
    {
        var line = new string('b', CompressionLimits.MaxLineBytes);
        var split = JsonlLineSplitter.Split(FixtureFactory.Utf8(line + "\n"));

        Assert.Equal(0, split.TooLongLineCount);
        Assert.Single(split.Lines);
        Assert.Equal(CompressionLimits.MaxLineBytes, split.Lines[0].Length);
    }
}
