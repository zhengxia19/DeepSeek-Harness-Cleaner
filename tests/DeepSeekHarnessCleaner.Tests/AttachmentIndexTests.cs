using System.IO;
using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-15 附件引用证据索引（TC-14：未证明孤儿时标 Unknown，无自动删除）。</summary>
public sealed class AttachmentIndexTests
{
    private static string Transcript(string sessionId, params string[] eventLines)
    {
        var lines = new List<string> { FixtureFactory.HeaderLine(sessionId, 0, "2026-10-01T00:00:00Z") };
        lines.AddRange(eventLines);
        return string.Join("\n", lines) + "\n";
    }

    private static SessionAttachmentSource Valid(string sessionId, params string[] eventLines) =>
        new(sessionId, Transcript(sessionId, eventLines), ParseStatus.Valid);

    private static AttachmentEvidence? Find(AttachmentIndexResult result, string attachmentId) =>
        result.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);

    [Fact]
    public void SharedAttachment_AcrossSessions_RemainsReferenced()
    {
        var index = new AttachmentIndex();
        var sessions = new[]
        {
            Valid("s-1", FixtureFactory.AttachmentRefLine("att-001")),
            Valid("s-2", FixtureFactory.MessageLine("user", "hi", "2026-10-01T00:00:01Z"),
                  FixtureFactory.AttachmentRefLine("att-001")),
        };

        var result = index.Build(sessions, ["att-001"]);

        Assert.True(result.GlobalOrphanConclusionAvailable);
        var evidence = Find(result, "att-001");
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentEvidenceStatus.Referenced, evidence.Status);
        Assert.Equal(new[] { "s-1", "s-2" }, evidence.ReferencingSessionIds);
    }

    [Fact]
    public void UnresolvedSession_OrphanConclusionUnavailable_UnreferencedBecomesUnknown()
    {
        var index = new AttachmentIndex();
        var sessions = new[]
        {
            Valid("s-1", FixtureFactory.AttachmentRefLine("att-001")),
            new SessionAttachmentSource("s-2", FixtureFactory.CorruptLine + "\n", ParseStatus.Corrupt),
        };

        var result = index.Build(sessions, ["att-001", "att-002"]);

        Assert.False(result.GlobalOrphanConclusionAvailable);
        Assert.Equal(new[] { "s-2" }, result.UnresolvedSessionIds);
        Assert.Equal(AttachmentEvidenceStatus.Referenced, Find(result, "att-001")!.Status);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, Find(result, "att-002")!.Status);
        Assert.Empty(Find(result, "att-002")!.ReferencingSessionIds);
    }

    [Fact]
    public void AllSessionsResolved_UnreferencedAttachment_ConfirmedUnreferenced()
    {
        var index = new AttachmentIndex();
        var sessions = new[] { Valid("s-1", FixtureFactory.MessageLine("user", "hi", "2026-10-01T00:00:01Z")) };

        var result = index.Build(sessions, ["att-002"]);

        Assert.True(result.GlobalOrphanConclusionAvailable);
        Assert.Equal(AttachmentEvidenceStatus.ConfirmedUnreferenced, Find(result, "att-002")!.Status);
    }

    [Fact]
    public void EmptySessionList_OrphanConclusionUnavailable()
    {
        var index = new AttachmentIndex();

        var result = index.Build([], ["att-002"]);

        Assert.False(result.GlobalOrphanConclusionAvailable);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, Find(result, "att-002")!.Status);
    }

    [Fact]
    public void MalformedAttachmentRef_DoesNotEstablishReference()
    {
        var index = new AttachmentIndex();
        var transcript = string.Join(
            "\n",
            FixtureFactory.HeaderLine("s-1", 0, "2026-10-01T00:00:00Z"),
            "{\"type\":\"attachment_ref\",\"attachmentId\":42}",
            "{\"type\":\"attachment_ref\"}",
            "{\"type\":\"attachment_ref\",\"attachmentId\":\"   \"}",
            FixtureFactory.CorruptLine,
            FixtureFactory.UnknownEventLine) + "\n";
        var sessions = new[] { new SessionAttachmentSource("s-1", transcript, ParseStatus.Valid) };

        var result = index.Build(sessions, ["att-001"]);

        Assert.Equal(AttachmentEvidenceStatus.Unknown, Find(result, "att-001")!.Status);
        Assert.False(result.GlobalOrphanConclusionAvailable);
        Assert.Empty(result.DanglingReferences);
    }

    [Fact]
    public void DanglingReference_RecordedAsEvidence_WithoutFabricatingAttachment()
    {
        var index = new AttachmentIndex();
        var sessions = new[] { Valid("s-1", FixtureFactory.AttachmentRefLine("att-999")) };

        var result = index.Build(sessions, ["att-001"]);

        Assert.Null(Find(result, "att-999"));
        var dangling = Assert.Single(result.DanglingReferences);
        Assert.Equal("att-999", dangling.AttachmentId);
        Assert.Equal(new[] { "s-1" }, dangling.SessionIds);
    }

    [Fact]
    public void DuplicateReferencesInSameSession_SessionListedOnce()
    {
        var index = new AttachmentIndex();
        var sessions = new[]
        {
            Valid("s-1", FixtureFactory.AttachmentRefLine("att-001"), FixtureFactory.AttachmentRefLine("att-001")),
        };

        var result = index.Build(sessions, ["att-001"]);

        Assert.Equal(new[] { "s-1" }, Find(result, "att-001")!.ReferencingSessionIds);
    }

    [Fact]
    public void PartialSession_ReferencesStillCount_ButOrphanConclusionBlocked()
    {
        var index = new AttachmentIndex();
        var partial = new SessionAttachmentSource(
            "s-2",
            Transcript("s-2", FixtureFactory.AttachmentRefLine("att-001")) + FixtureFactory.CorruptLine + "\n",
            ParseStatus.Partial);
        var sessions = new[] { Valid("s-1", FixtureFactory.MessageLine("user", "hi", "2026-10-01T00:00:01Z")), partial };

        var result = index.Build(sessions, ["att-001", "att-002"]);

        Assert.False(result.GlobalOrphanConclusionAvailable);
        Assert.Equal(AttachmentEvidenceStatus.Referenced, Find(result, "att-001")!.Status);
        Assert.Contains("s-2", Find(result, "att-001")!.ReferencingSessionIds);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, Find(result, "att-002")!.Status);
    }

    [Fact]
    public void Build_IsReadOnly_AttachmentFilesOnDiskUntouched()
    {
        using var fx = FixtureRoot.Create();
        var att1 = fx.Factory.WriteFile(fx.AttachmentsRoot, "att-001.bin", FixtureFactory.Utf8("payload-1"));
        var att2 = fx.Factory.WriteFile(fx.AttachmentsRoot, "att-002.bin", FixtureFactory.Utf8("payload-2"));
        var before1 = File.ReadAllBytes(att1);
        var before2 = File.ReadAllBytes(att2);

        var index = new AttachmentIndex();
        var sessions = new[] { Valid("s-1", FixtureFactory.AttachmentRefLine("att-001")) };
        var result = index.Build(sessions, ["att-001", "att-002"]);

        Assert.True(File.Exists(att1));
        Assert.True(File.Exists(att2));
        Assert.Equal(before1, File.ReadAllBytes(att1));
        Assert.Equal(before2, File.ReadAllBytes(att2));
        Assert.Equal(AttachmentEvidenceStatus.ConfirmedUnreferenced, Find(result, "att-002")!.Status);
    }

    [Fact]
    public void NullTranscript_SessionUnresolved_AndNoReference()
    {
        var index = new AttachmentIndex();
        var sessions = new[] { new SessionAttachmentSource("s-1", null, ParseStatus.Unreadable) };

        var result = index.Build(sessions, ["att-001"]);

        Assert.False(result.GlobalOrphanConclusionAvailable);
        Assert.Equal(new[] { "s-1" }, result.UnresolvedSessionIds);
        Assert.Equal(AttachmentEvidenceStatus.Unknown, Find(result, "att-001")!.Status);
    }
}
