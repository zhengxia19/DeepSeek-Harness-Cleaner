using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-21：ZIP manifest 构建、zip-slip/去重/未知文件拒绝、清单签名与可复算校验（TC-09）。</summary>
public sealed class ManifestTests
{
    private static readonly Regex LowerHex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private static PhysicalLog Log(string full, int generation, ParseStatus status = ParseStatus.Valid) =>
        new(full, generation, false, new FileInfo(full).Length, DateTimeOffset.UnixEpoch, null, status);

    private static SessionSummary Session(string? id, params PhysicalLog[] logs) =>
        new(id, "proj", null, null, DateOrigin.FileMtime, logs, ParseStatus.Valid);

    private static string ExpectedSha(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    [Fact]
    public void Build_CountsAllGenerations_WithNormalizedZipPaths()
    {
        using var fx = FixtureRoot.Create();
        var v0 = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("v0\n"));
        var v4 = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.v4.jsonl", FixtureFactory.Utf8("v4\n"));

        var result = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(v0, 0), Log(v4, 4))]);

        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Manifest!.Entries.Count);
        Assert.Equal(
            new[] { "sessions/proj-a/sess-1/session.jsonl", "sessions/proj-a/sess-1/session.v4.jsonl" },
            result.Manifest.Entries.Select(e => e.ZipPath).ToArray());
        Assert.All(result.Manifest.Entries, e =>
        {
            Assert.Equal(e.ZipPath, e.RelativePath);
            Assert.Equal("sess-1", e.SessionId);
            Assert.Empty(e.Warnings);
            Assert.Matches(LowerHex64, e.Sha256);
        });
    }

    [Fact]
    public void Build_RecordsLowercaseSha256AndExactLength()
    {
        using var fx = FixtureRoot.Create();
        var content = FixtureFactory.Utf8("synthetic session body\n");
        var file = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", content);

        var result = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(file, 0))]);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Manifest!.Entries);
        Assert.Equal(content.LongLength, entry.Length);
        Assert.Equal(ExpectedSha(content), entry.Sha256);
    }

    [Fact]
    public void Build_IncludesAttachmentsUnderAttachmentsCategory()
    {
        using var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var attachment = fx.Factory.WriteFile(fx.AttachmentsRoot, "att-1/blob.bin", FixtureFactory.Utf8("blob"));

        var result = new BackupManifestBuilder().Build(
            fx.DshHome,
            [Session("sess-1", Log(session, 0))],
            [attachment]);

        Assert.True(result.Success);
        Assert.Equal(2, result.Manifest!.Entries.Count);
        var att = result.Manifest.Entries.Single(e => e.ZipPath == "attachments/att-1/blob.bin");
        Assert.Null(att.SessionId);
    }

    [Fact]
    public void Build_WarnsOnNonValidStatus_ButSucceeds()
    {
        using var fx = FixtureRoot.Create();
        var file = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var session = new SessionSummary(
            "sess-1", "proj", null, null, DateOrigin.FileMtime,
            [Log(file, 0, ParseStatus.Corrupt)], ParseStatus.Corrupt);

        var result = new BackupManifestBuilder().Build(fx.DshHome, [session]);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Manifest!.Entries);
        Assert.Contains("session-status-Corrupt", entry.Warnings);
        Assert.Contains("generation-status-Corrupt", entry.Warnings);
    }

    [Fact]
    public void Build_RejectsPathTraversalSegments()
    {
        using var fx = FixtureRoot.Create();
        var file = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var traversing = Path.Combine(fx.SessionsRoot, "proj-a", "sess-1", "..", "..", "escape", "session.jsonl");
        fx.Factory.WriteFile(fx.SessionsRoot, Path.Combine("escape", "session.jsonl"), FixtureFactory.Utf8("x\n"));

        var result = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(file, 0), Log(traversing, 4))]);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Code == "PATH_TRAVERSAL");
    }

    [Fact]
    public void Build_RejectsCrossRootFiles()
    {
        using var fx = FixtureRoot.Create();
        var outside = fx.Factory.WriteFile(fx.Root, Path.Combine("outside", "session.jsonl"), FixtureFactory.Utf8("x\n"));

        var result = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(outside, 0))]);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Code == "CROSS_ROOT");
    }

    [Fact]
    public void Build_RejectsUnknownCategoryFiles()
    {
        using var fx = FixtureRoot.Create();
        var storageFile = fx.Factory.WriteFile(fx.StoragesRoot, "session_projcache.json", FixtureFactory.Utf8("{}"));

        var result = new BackupManifestBuilder().Build(fx.DshHome, [], [storageFile]);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Code == "UNKNOWN_FILE");
    }

    [Fact]
    public void Build_RejectsDuplicateNormalizedEntries()
    {
        using var fx = FixtureRoot.Create();
        var file = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var sameDifferentCase = Path.Combine(fx.SessionsRoot, "PROJ-A", "SESS-1", "SESSION.JSONL");

        var result = new BackupManifestBuilder().Build(
            fx.DshHome,
            [Session("sess-1", Log(file, 0)), Session("sess-2", Log(sameDifferentCase, 0))]);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Code == "DUPLICATE_ENTRY");
    }

    [Fact]
    public void Verify_RecomputesManifestFromDisk_AndPasses()
    {
        using var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var built = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(session, 0))]);
        Assert.True(built.Success);

        var verified = new BackupManifestBuilder().Verify(built.Manifest!, fx.DshHome, [session]);

        Assert.True(verified.Success);
        Assert.Empty(verified.Issues);
    }

    [Fact]
    public void Verify_DetectsTamperedFile()
    {
        using var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var built = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(session, 0))]);
        Assert.True(built.Success);
        File.WriteAllBytes(session, FixtureFactory.Utf8("tampered\n"));

        var verified = new BackupManifestBuilder().Verify(built.Manifest!, fx.DshHome);

        Assert.False(verified.Success);
        Assert.Contains(verified.Issues, i => i.Code == "SHA_MISMATCH");
        Assert.Contains(verified.Issues, i => i.Code == "LENGTH_MISMATCH");
    }

    [Fact]
    public void Verify_DetectsMissingFile()
    {
        using var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var built = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(session, 0))]);
        Assert.True(built.Success);
        File.Delete(session);

        var verified = new BackupManifestBuilder().Verify(built.Manifest!, fx.DshHome);

        Assert.False(verified.Success);
        Assert.Contains(verified.Issues, i => i.Code == "MISSING_FILE");
    }

    [Fact]
    public void Verify_RejectsUnknownActualFile()
    {
        using var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("s\n"));
        var extra = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.v9.jsonl", FixtureFactory.Utf8("x\n"));
        var built = new BackupManifestBuilder().Build(fx.DshHome, [Session("sess-1", Log(session, 0))]);
        Assert.True(built.Success);

        var verified = new BackupManifestBuilder().Verify(built.Manifest!, fx.DshHome, [session, extra]);

        Assert.False(verified.Success);
        Assert.Contains(verified.Issues, i => i.Code == "UNKNOWN_FILE");
    }

    [Fact]
    public void Signature_StableAcrossJsonRoundTrip_AndDetectsTampering()
    {
        using var fx = FixtureRoot.Create();
        var a = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("a\n"));
        var b = fx.Factory.WriteSessionFile("proj-b", "sess-2", "session.v4.jsonl", FixtureFactory.Utf8("bb\n"));
        var built = new BackupManifestBuilder().Build(
            fx.DshHome,
            [Session("sess-2", Log(b, 4)), Session("sess-1", Log(a, 0))]);
        Assert.True(built.Success);

        var signature = BackupManifestBuilder.ComputeSignature(built.Manifest!);
        Assert.Matches(LowerHex64, signature);

        var json = BackupManifestBuilder.ToJson(built.Manifest!);
        var loaded = BackupManifestBuilder.FromJson(json);
        Assert.True(loaded.Success);
        Assert.Equal(json, BackupManifestBuilder.ToJson(loaded.Manifest!));
        Assert.Equal(signature, BackupManifestBuilder.ComputeSignature(loaded.Manifest!));

        var firstSha = built.Manifest!.Entries[0].Sha256;
        var tamperedJson = json.Replace("\"" + firstSha + "\"", "\"" + new string('0', 64) + "\"", StringComparison.Ordinal);
        var tampered = BackupManifestBuilder.FromJson(tamperedJson);
        Assert.True(tampered.Success);
        var verified = new BackupManifestBuilder().Verify(tampered.Manifest!, fx.DshHome, expectedSignature: signature);
        Assert.False(verified.Success);
        Assert.Contains(verified.Issues, i => i.Code == "SIGNATURE_MISMATCH");
    }

    [Fact]
    public void FromJson_RejectsUnsupportedSchemaAndMalformedEntries()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dshcleaner-manifest-nonexistent"));
        string Entry(string zip, long length, string sha) =>
            "{\"zipPath\":\"" + zip + "\",\"relativePath\":\"" + zip + "\",\"length\":" + length +
            ",\"sha256\":\"" + sha + "\",\"sessionId\":null,\"warnings\":[]}";
        string Doc(string schemaVersion, string entries) =>
            "{\"schemaVersion\":" + schemaVersion + ",\"root\":\"" + root.Replace("\\", "\\\\") + "\",\"entries\":[" + entries + "]}";
        var sha = new string('a', 64);

        Assert.Equal("UNSUPPORTED_SCHEMA", BackupManifestBuilder.FromJson(Doc("2", Entry("sessions/a", 1, sha))).Issues.Single().Code);
        Assert.Equal("INVALID_MANIFEST", BackupManifestBuilder.FromJson(Doc("1", Entry("sessions/a", 1, sha.ToUpperInvariant()))).Issues.Single().Code);
        Assert.Equal("INVALID_MANIFEST", BackupManifestBuilder.FromJson(Doc("1", Entry("sessions/../escape", 1, sha))).Issues.Single().Code);
        Assert.Equal("UNKNOWN_FILE", BackupManifestBuilder.FromJson(Doc("1", Entry("storages/x.json", 1, sha))).Issues.Single().Code);
        Assert.Equal(
            "DUPLICATE_ENTRY",
            BackupManifestBuilder.FromJson(Doc("1", Entry("sessions/a", 1, sha) + "," + Entry("sessions/A", 1, sha))).Issues.Single().Code);
    }
}
