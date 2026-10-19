using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-12 安全 JSONL 事件流（TC-04、TC-20）。有效 JSONL 行数与顺序一致；
/// 无效行明确报损坏/部分；取消与超限不泄漏句柄；读取不修改日志。
/// </summary>
public sealed class EventReaderTests
{
    private static readonly SessionEventReader Reader = new();

    [Fact]
    public void KnownEventTypes_MatchesRealRootVocabulary_M03()
    {
        // M-03：真实根实测 47 类事件词表 + attachment_ref（按 D7 保留）= 48 项；
        // 词表证据见 artifacts/evidence/M-01.txt（仅类型名与频次）。
        Assert.Equal(48, SessionEventReader.KnownEventTypes.Count);
        foreach (var type in new[]
        {
            "session", "user/message", "assistant/message", "system/message", "developer/message",
            "tool/result", "reasoning-chunks", "tool-call-chunks", "text-chunks", "attachment_ref",
        })
        {
            Assert.Contains(type, SessionEventReader.KnownEventTypes);
        }
    }

    [Fact]
    public void PlainSession_LineCountAndOrderMatch()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV0Session();

        var result = Reader.Read(path);

        Assert.Equal(ParseStatus.Valid, result.Status);
        Assert.Equal([1, 2], result.Lines.Select(l => l.LineNumber));
        Assert.Equal(["session", "user/message"], result.Lines.Select(l => l.EventType));
        Assert.All(result.Lines, l => Assert.Equal(EventLineStatus.Valid, l.Status));
    }

    [Fact]
    public void MultiFrameZstdSession_LineCountAndOrderMatch()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();
        var path = Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001", "session.v4.jsonl.zstd");

        var result = Reader.Read(path);

        // TC-04：多帧按序解码后行流与明文一致。
        Assert.Equal(ParseStatus.Valid, result.Status);
        Assert.Equal([1, 2], result.Lines.Select(l => l.LineNumber));
        Assert.Equal(["session", "user/message"], result.Lines.Select(l => l.EventType));
    }

    [Fact]
    public void IncompleteJsonLine_ReportedMalformed_FilePartial()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteCorruptJsonSession();

        var result = Reader.Read(path);

        // TC-04：不完整 JSON 明确标记，不静默丢弃。
        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(EventLineStatus.Valid, result.Lines[0].Status);
        Assert.Equal(EventLineStatus.MalformedJson, result.Lines[1].Status);
        Assert.Equal(2, result.Lines[1].LineNumber);
        Assert.Equal(1, result.MalformedCount);
    }

    [Fact]
    public void TruncatedZstdTail_ReportedPartial()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteTruncatedZstdSession();

        var result = Reader.Read(path);

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(SessionEventReader.ZstdTruncatedErrorCode, result.ErrorCode);
    }

    [Fact]
    public void UnknownEvent_ReportedWithTypeAndLineNumber_NotGuessed()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteUnknownEventSession();

        var result = Reader.Read(path);

        // design.md §48：未知事件输出类型/行号摘要；文件整体仍可读。
        Assert.Equal(ParseStatus.Valid, result.Status);
        var unknown = result.Lines.Single(l => l.Status == EventLineStatus.Unknown);
        Assert.Equal(2, unknown.LineNumber);
        Assert.Equal("quantum_event", unknown.EventType);
        Assert.Equal(1, result.UnknownCount);
    }

    [Fact]
    public void EmptyFile_ReportedCorrupt()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteEmptySession();

        var result = Reader.Read(path);

        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionEventReader.EmptyFileErrorCode, result.ErrorCode);
    }

    [Fact]
    public void AllLinesMalformed_ReportedCorrupt()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile("proj-beta", "synth-allbad-001", "session.v4.jsonl",
            FixtureFactory.Utf8(FixtureFactory.CorruptLine + "\n" + FixtureFactory.CorruptLine + "\n"));

        var result = Reader.Read(path);

        Assert.Equal(ParseStatus.Corrupt, result.Status);
        Assert.Equal(SessionEventReader.AllLinesMalformedErrorCode, result.ErrorCode);
        Assert.Equal(2, result.MalformedCount);
    }

    [Fact]
    public void NonObjectLine_ReportedNotObject_FilePartial()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteSessionFile("proj-beta", "synth-notobj-001", "session.v4.jsonl",
            FixtureFactory.Utf8(FixtureFactory.HeaderLine("synth-notobj-001", 4, "2026-03-05T12:00:00Z") + "\n[1,2,3]\n"));

        var result = Reader.Read(path);

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(EventLineStatus.NotObject, result.Lines[1].Status);
    }

    [Fact]
    public void TooLongLine_NumberPreservedBodyDropped_FilePartial()
    {
        using var fx = FixtureRoot.Create();
        var longLine = "{\"type\":\"message\",\"text\":\"" + new string('x', 200) + "\"}";
        var path = fx.Factory.WriteSessionFile("proj-beta", "synth-long-001", "session.v4.jsonl",
            FixtureFactory.Utf8(
                FixtureFactory.HeaderLine("synth-long-001", 4, "2026-03-06T12:00:00Z") + "\n"
                + longLine + "\n"
                + FixtureFactory.MessageLine("user", "after long", "2026-03-06T12:01:00Z") + "\n"));

        var result = Reader.Read(path, maxLineBytes: 128);

        // 超限行保留行号、丢弃正文；后续行号连续正确。
        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal([1, 2, 3], result.Lines.Select(l => l.LineNumber));
        Assert.Equal(EventLineStatus.TooLong, result.Lines[1].Status);
        Assert.Null(result.Lines[1].EventType);
        Assert.Equal(EventLineStatus.Valid, result.Lines[2].Status);
    }

    [Fact]
    public void TotalLimitExceeded_PartialAndNoHandleLeak()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV4Session();

        var result = Reader.Read(path, maxTotalBytes: 16);

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(SessionEventReader.TotalLimitErrorCode, result.ErrorCode);
        // 超限后句柄必须已释放：删除成功即证明无泄漏。
        File.Delete(path);
    }

    [Fact]
    public void Cancellation_ThrowsAndNoHandleLeak()
    {
        using var fx = FixtureRoot.Create();
        var path = fx.Factory.WriteValidV0Session();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Reader.Read(path, cancellationToken: cts.Token));

        // 取消路径句柄已释放（using），删除成功即证明无泄漏。
        File.Delete(path);
    }

    [Fact]
    public void MissingFile_ReportedUnreadable_NoThrow()
    {
        using var fx = FixtureRoot.Create();
        var path = Path.Combine(fx.SessionsRoot, "proj-missing", "synth-missing-001", "session.jsonl");

        var result = Reader.Read(path);

        // TC-20：异常路径不卡死、不抛到调用方。
        Assert.Equal(ParseStatus.Unreadable, result.Status);
        Assert.Equal(SessionEventReader.FileUnreadableErrorCode, result.ErrorCode);
    }

    [Fact]
    public void ReadNeverModifiesLogFile_BytesIdentical()
    {
        using var fx = FixtureRoot.Create();
        var plain = fx.Factory.WriteValidV0Session();
        var zstd = fx.Factory.WriteValidV4Session();
        var plainBefore = File.ReadAllBytes(plain);
        var zstdBefore = File.ReadAllBytes(zstd);

        Assert.True(Reader.Read(plain).IsReadComplete);
        Assert.True(Reader.Read(zstd).IsReadComplete);

        Assert.Equal(plainBefore, File.ReadAllBytes(plain));
        Assert.Equal(zstdBefore, File.ReadAllBytes(zstd));
    }
}
