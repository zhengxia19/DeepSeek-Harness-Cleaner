using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Infrastructure.Compression;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-09 受限日志头部读取（TC-03、TC-04）。全部在夹具合成根下，只读断言源字节不变。</summary>
public sealed class HeaderTests
{
    private static readonly SessionHeaderReader Reader = new();

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public void ValidV4_Plain_HeaderExtracted()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV4Session();

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Valid, result.Status);
        Assert.NotNull(result.Header);
        Assert.Equal("synth-v4-001", result.Header!.SessionId);
        Assert.Equal(4, result.Header.FormatVersion);
        Assert.Equal(new DateTimeOffset(2026, 2, 10, 10, 0, 0, TimeSpan.Zero), result.Header.CreatedAt);
        Assert.Null(result.Header.Cwd);
        Assert.True(result.AllowsAutoCleanup);
    }

    [Fact]
    public void ValidV0_Legacy_HeaderExtracted()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV0Session();

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Valid, result.Status);
        Assert.Equal(0, result.Header!.FormatVersion);
        Assert.Equal("synth-v0-001", result.Header.SessionId);
        Assert.True(result.AllowsAutoCleanup);
    }

    [Fact]
    public void MultiGeneration_ZstdAndPlain_BothExtracted_ConsistentIds()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();
        var dir = Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001");

        var v0 = Reader.Read(Path.Combine(dir, "session.jsonl"), "synth-multi-001");
        var v4 = Reader.Read(Path.Combine(dir, "session.v4.jsonl.zstd"), "synth-multi-001");

        Assert.Equal(ParseStatus.Valid, v0.Status);
        Assert.Equal(0, v0.Header!.FormatVersion);
        Assert.Equal(ParseStatus.Valid, v4.Status);
        Assert.Equal(4, v4.Header!.FormatVersion);
        Assert.True(v0.IdentityMatches);
        Assert.True(v4.IdentityMatches);
        // TC-03：同一会话两代际 header 身份一致 → 允许清理。
        Assert.True(SessionHeaderReader.HeadersConsistent(new[] { v0, v4 }));
    }

    [Fact]
    public void TruncatedZstd_Partial_NotDeletable()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteTruncatedZstdSession();

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(SessionHeaderReader.ZstdTruncatedErrorCode, result.ErrorCode);
        Assert.False(result.AllowsAutoCleanup);
        Assert.False(SessionHeaderReader.HeadersConsistent(new[] { result }));
    }

    [Fact]
    public void MalformedFirstLine_Corrupt_NotDeletable()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile(
            "proj-h", "s-malformed", "session.v4.jsonl",
            FixtureFactory.Utf8(FixtureFactory.CorruptLine + "\n"));

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderMalformedErrorCode, result.ErrorCode);
        Assert.False(result.AllowsAutoCleanup);
    }

    [Fact]
    public void FirstLineNotHeader_MissingHeader_Corrupt()
    {
        using var fx = FixtureRoot.Create();
        var line = FixtureFactory.MessageLine("user", "no header here", "2026-06-01T00:00:00Z");
        var path = fx.Factory.WriteSessionFile(
            "proj-h", "s-noheader", "session.v4.jsonl",
            FixtureFactory.Utf8(line + "\n"));

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderMissingErrorCode, result.ErrorCode);
    }

    [Fact]
    public void EmptyFile_MissingHeader_Corrupt()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteEmptySession();

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderMissingErrorCode, result.ErrorCode);
    }

    [Fact]
    public void UnsupportedFormatVersion_Flagged_NotDeletable()
    {
        using var fx = FixtureRoot.Create();
        var line = FixtureFactory.HeaderLine("synth-unsupported-001", 9, "2026-04-01T00:00:00Z");
        var path = fx.Factory.WriteSessionFile(
            "proj-h", "s-unsupported", "session.v4.jsonl", FixtureFactory.Utf8(line + "\n"));

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Unsupported, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderVersionUnsupportedErrorCode, result.ErrorCode);
        Assert.False(result.AllowsAutoCleanup);
    }

    [Fact]
    public void InvalidCreatedAt_Corrupt()
    {
        using var fx = FixtureRoot.Create();
        var line = FixtureFactory.HeaderLine("synth-baddate-001", 4, "not-a-date");
        var path = fx.Factory.WriteSessionFile(
            "proj-h", "s-baddate", "session.v4.jsonl", FixtureFactory.Utf8(line + "\n"));

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderDateInvalidErrorCode, result.ErrorCode);
    }

    [Fact]
    public void HeaderLineExceedingLimit_Corrupt()
    {
        using var fx = FixtureRoot.Create();
        var padding = new string('a', CompressionLimits.MaxLineBytes + 16);
        var path = fx.Factory.WriteSessionFile(
            "proj-h", "s-toolong", "session.jsonl",
            Encoding.UTF8.GetBytes("{\"type\":\"header\",\"sessionId\":\"" + padding + "\"}\n"));

        var result = Reader.Read(path);
        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionHeaderReader.HeaderLineTooLongErrorCode, result.ErrorCode);
    }

    [Fact]
    public void IdentityMismatch_Flagged_NotDeletable()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV4Session();

        var mismatch = Reader.Read(path, expectedSessionId: "some-other-id");
        Assert.Equal(ParseStatus.Valid, mismatch.Status);
        Assert.False(mismatch.IdentityMatches);
        Assert.False(mismatch.AllowsAutoCleanup);

        var match = Reader.Read(path, expectedSessionId: "synth-v4-001");
        Assert.True(match.IdentityMatches);
        Assert.True(match.AllowsAutoCleanup);
    }

    [Fact]
    public void ConflictingHeadersAcrossGenerations_NotConsistent()
    {
        using var fx = FixtureRoot.Create();
        var dir = Path.Combine(fx.SessionsRoot, "proj-h", "s-conflict");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.jsonl"),
            FixtureFactory.HeaderLine("id-a", 0, "2026-01-01T00:00:00Z") + "\n");
        File.WriteAllBytes(Path.Combine(dir, "session.v4.jsonl.zstd"),
            FixtureFactory.ZstdFrames(FixtureFactory.HeaderLine("id-b", 4, "2026-01-02T00:00:00Z") + "\n"));

        var a = Reader.Read(Path.Combine(dir, "session.jsonl"));
        var b = Reader.Read(Path.Combine(dir, "session.v4.jsonl.zstd"));
        Assert.Equal(ParseStatus.Valid, a.Status);
        Assert.Equal(ParseStatus.Valid, b.Status);
        // design.md §2：header 身份冲突 → 不进入可删白名单。
        Assert.False(SessionHeaderReader.HeadersConsistent(new[] { a, b }));
    }

    [Fact]
    public void Read_IsReadOnly_SourceBytesUnchanged()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();
        var plain = Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001", "session.jsonl");
        var zstd = Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001", "session.v4.jsonl.zstd");
        var plainBefore = Sha256(plain);
        var zstdBefore = Sha256(zstd);

        Reader.Read(plain, "synth-multi-001");
        Reader.Read(zstd, "synth-multi-001");

        Assert.Equal(plainBefore, Sha256(plain));
        Assert.Equal(zstdBefore, Sha256(zstd));
    }
}
