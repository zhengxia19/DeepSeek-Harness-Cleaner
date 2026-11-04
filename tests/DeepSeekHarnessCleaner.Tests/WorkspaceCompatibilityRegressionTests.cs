using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T03: current read-only behavior, not metadata write authorization.</summary>
public sealed class WorkspaceCompatibilityRegressionTests
{
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    [Fact]
    public void ArchiveReader_ReportsOnlyArchiveIds_NotPinsMembersOrPhysicalLogExistence()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var before = Hash(fixture.WorkspacePath);

        var result = new WorkspaceArchiveReader().Read(root.DshHome);

        Assert.Equal(WorkspaceFixture.ArchivedId, Assert.Single(result!));
        Assert.Empty(Directory.GetFiles(root.SessionsRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(before, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unknown-version")]
    [InlineData("uninitialized")]
    [InlineData("duplicate-archive")]
    public void ArchiveReader_InvalidFixtureIsUnknown_NotKnownEmpty(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write(scenario);
        var before = Hash(fixture.WorkspacePath);
        Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
        Assert.Equal(before, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("missing-archive")]
    [InlineData("null-global")]
    [InlineData("wrong-unit")]
    [InlineData("string-version")]
    [InlineData("pin-overlap")]
    [InlineData("invalid-pin")]
    [InlineData("blank-archive")]
    public void ArchiveReader_InvalidFieldsDoNotBecomeKnownEmpty(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var document = fixture.CreateDocument();
        switch (scenario)
        {
            case "missing-archive": document["global"]!.AsObject().Remove("archivedSessionIds"); break;
            case "null-global": document["global"] = null; break;
            case "wrong-unit": document["unit"]!["name"] = "unknown"; break;
            case "string-version": document["unit"]!["version"] = "2"; break;
            case "pin-overlap": document["global"]!["pinnedSessionIds"] = new JsonArray(WorkspaceFixture.ArchivedId); break;
            case "invalid-pin": document["global"]!["pinnedSessionIds"] = new JsonArray(42); break;
            case "blank-archive": document["global"]!["archivedSessionIds"] = new JsonArray(" "); break;
        }
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(document.ToJsonString()));
        var before = Hash(fixture.WorkspacePath);
        Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
        Assert.Equal(before, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("{\"unit\":{},\"unit\":{\"name\":\"workspace\",\"version\":2},\"global\":{\"initialized\":true,\"archivedSessionIds\":[]}}")]
    [InlineData("{\"unit\":{\"name\":\"workspace\",\"version\":1,\"version\":2},\"global\":{\"initialized\":true,\"archivedSessionIds\":[]}}")]
    [InlineData("{\"unit\":{\"name\":\"workspace\",\"version\":2},\"global\":{\"initialized\":false,\"initialized\":true,\"archivedSessionIds\":[]}}")]
    public void ArchiveReader_DuplicateObjectFieldsReturnUnknown(string json)
    {
        using var root = FixtureRoot.Create();
        var path = root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(json));
        var before = Hash(path);
        Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void ArchiveReader_MissingLockedAndEmptyHaveDifferentResults()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var reader = new WorkspaceArchiveReader();
        Assert.Null(reader.Read(root.DshHome));
        Assert.False(File.Exists(fixture.WorkspacePath));
        var document = fixture.CreateDocument();
        document["global"]!["archivedSessionIds"] = new JsonArray();
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(document.ToJsonString()));
        var before = Hash(fixture.WorkspacePath);
        using (var locked = new FileStream(fixture.WorkspacePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(reader.Read(root.DshHome));
        Assert.Empty(reader.Read(root.DshHome)!);
        Assert.Equal(before, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("pending-create")]
    [InlineData("pending-delete")]
    [InlineData("duplicate-member")]
    public void ArchiveReader_CurrentArchiveOnlyScopeDoesNotValidateFullWorkspace(string scenario)
    {
        // Characterization only: T07 must independently reject these states for a full reference index.
        // A non-null archive result is never a transaction or write-safety gate.
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write(scenario);
        var before = Hash(fixture.WorkspacePath);
        Assert.Equal(WorkspaceFixture.ArchivedId, Assert.Single(new WorkspaceArchiveReader().Read(root.DshHome)!));
        Assert.Equal(before, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unknown-version")]
    [InlineData("corrupt")]
    public void PhysicalDiscoveryAndCacheMapping_DoNotInferLogsFromWorkspace(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write(scenario);
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        // Physical discovery must preserve both generations, even if contents are not parseable.
        root.Factory.WriteSessionFile("synthetic-a", WorkspaceFixture.ArchivedId, "session.jsonl", FixtureFactory.Utf8("synthetic invalid log"));
        root.Factory.WriteProjectionCache(WorkspaceFixture.ArchivedId);
        root.Factory.WriteProjectionCache(WorkspaceFixture.MissingId);
        var before = Directory.GetFiles(root.Root, "*", SearchOption.AllDirectories).ToDictionary(x => x, Hash);
        var discovery = new SessionLogDiscovery(new PathGuard()).Discover(root.SessionsRoot);
        Assert.True(discovery.RootValid);
        var candidate = Assert.Single(discovery.Candidates);
        Assert.False(candidate.IsAmbiguous);
        Assert.Equal(new[] { 0, 4 }, candidate.Files.Select(x => x.Generation).OrderBy(x => x));
        var ids = discovery.Candidates.Select(x => Path.GetFileName(x.FullPath)).ToArray();
        var caches = new SessionCacheIndex(new PathGuard()).Build(root.StoragesRoot, ids);
        Assert.True(caches.RootValid);
        Assert.Equal(2, caches.DedicatedCaches.Count);
        Assert.Contains(caches.DedicatedCaches, x => x.SessionId == WorkspaceFixture.ArchivedId && x.Status == SessionCacheStatus.Matched);
        Assert.Contains(caches.DedicatedCaches, x => x.Status == SessionCacheStatus.Unmatched && x.Reason == SessionCacheIndex.NotInInventoryReason);
        Assert.DoesNotContain(caches.WholeCacheCandidates, x => x.FullPath == fixture.WorkspacePath);
        Assert.Equal(2, caches.WholeCacheCandidates.Count);
        Assert.Equal(before.Keys.OrderBy(x => x), Directory.GetFiles(root.Root, "*", SearchOption.AllDirectories).OrderBy(x => x));
        foreach (var file in before) Assert.Equal(file.Value, Hash(file.Key));
    }

    [Fact]
    public void SymbolicLinks_AreRejectedByAllThreeReaders_OutsideTargetUnchanged()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var target = new WorkspaceFixture(outside).Write("valid");
        var sentinelHash = Hash(target);
        var workspaceLink = Path.Combine(root.StoragesRoot, "workspace.json");
        var logDirectory = Path.Combine(root.SessionsRoot, "synthetic", "linked");
        var cacheDirectory = Path.Combine(root.StoragesRoot, "session_projcache", "sessions");
        Directory.CreateDirectory(Path.GetDirectoryName(logDirectory)!);
        Directory.CreateDirectory(cacheDirectory);
        var cacheLink = Path.Combine(cacheDirectory, WorkspaceFixture.ArchivedId + ".json");
        try
        {
            File.CreateSymbolicLink(workspaceLink, target);
            Directory.CreateSymbolicLink(logDirectory, outside.StoragesRoot);
            File.CreateSymbolicLink(cacheLink, target);
            Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
            var discovery = new SessionLogDiscovery(new PathGuard()).Discover(root.SessionsRoot);
            Assert.True(discovery.RootValid);
            Assert.Empty(discovery.Candidates);
            Assert.Contains(discovery.Anomalies, x => x.FullPath == logDirectory && x.Reason == SessionLogDiscovery.ReparseReason);
            var caches = new SessionCacheIndex(new PathGuard()).Build(root.StoragesRoot, [WorkspaceFixture.ArchivedId]);
            Assert.True(caches.RootValid);
            var entry = Assert.Single(caches.DedicatedCaches);
            Assert.Equal(SessionCacheStatus.Unknown, entry.Status);
            Assert.Equal(SessionCacheIndex.ReparseReason, entry.Reason);
            Assert.Equal(sentinelHash, Hash(target));
        }
        finally
        {
            if (File.Exists(workspaceLink)) File.Delete(workspaceLink);
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory);
            if (File.Exists(cacheLink)) File.Delete(cacheLink);
        }
        Assert.Equal(sentinelHash, Hash(target));
    }
}
