using System.Diagnostics;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-16 单会话投影缓存只读索引（TC-12 识别半边）。全部在夹具合成根下执行。</summary>
public sealed class CacheIndexTests
{
    private static readonly SessionCacheIndex Index = new(new PathGuard());

    private const string SessionsRel = "session_projcache/sessions";

    private static void CreateJunction(string linkPath, string targetDir)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDir}\"")
        {
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(15_000);
        Assert.Equal(0, process.ExitCode);
        Assert.True(Directory.Exists(linkPath));
    }

    private static string SessionPath(string storagesRoot, string id) =>
        Path.Combine(storagesRoot, SessionsRel.Replace('/', Path.DirectorySeparatorChar), id + ".json");

    private static SessionCacheEntry EntryFor(CacheIndexResult result, string fileName) =>
        Assert.Single(result.DedicatedCaches, e => e.RelativePath!.EndsWith(fileName, StringComparison.Ordinal));

    [Fact]
    public void MatchedAndUnmatched_AreMarkedSeparately()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-1.json", FixtureFactory.Utf8("{{\"v\":1}}"));
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-2.json", FixtureFactory.Utf8("{{\"v\":2}}"));
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-9.json", FixtureFactory.Utf8("{{\"v\":9}}"));

        var result = Index.Build(fx.StoragesRoot, new[] { "s-1", "s-2" });

        Assert.True(result.RootValid);
        var m1 = EntryFor(result, "s-1.json");
        Assert.Equal(SessionCacheStatus.Matched, m1.Status);
        Assert.Equal("s-1", m1.SessionId);
        Assert.Null(m1.Reason);
        var m2 = EntryFor(result, "s-2.json");
        Assert.Equal(SessionCacheStatus.Matched, m2.Status);
        Assert.Equal("s-2", m2.SessionId);
        var u = EntryFor(result, "s-9.json");
        Assert.Equal(SessionCacheStatus.Unmatched, u.Status);
        Assert.Null(u.SessionId);
        Assert.Equal(SessionCacheIndex.NotInInventoryReason, u.Reason);
    }

    [Fact]
    public void EmptyInventory_WellFormedCache_IsUnknown()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-1.json", FixtureFactory.Utf8("{}"));

        var result = Index.Build(fx.StoragesRoot, []);

        var entry = EntryFor(result, "s-1.json");
        Assert.Equal(SessionCacheStatus.Unknown, entry.Status);
        Assert.Equal(SessionCacheIndex.NoInventoryReason, entry.Reason);
    }

    [Fact]
    public void DirectoryEntry_IsUnknown_StructureMismatch()
    {
        using var fx = FixtureRoot.Create();
        Directory.CreateDirectory(SessionPath(fx.StoragesRoot, "d-1"));

        var result = Index.Build(fx.StoragesRoot, new[] { "d-1" });

        var entry = EntryFor(result, "d-1.json");
        Assert.Equal(SessionCacheStatus.Unknown, entry.Status);
        Assert.Equal(SessionCacheIndex.StructureMismatchReason, entry.Reason);
    }

    [Fact]
    public void NonJsonAndEmptyId_AreUnknown()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/notes.txt", FixtureFactory.Utf8("x"));
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/.json", FixtureFactory.Utf8("{}"));

        var result = Index.Build(fx.StoragesRoot, new[] { "notes", "" });

        var txt = EntryFor(result, "notes.txt");
        Assert.Equal(SessionCacheStatus.Unknown, txt.Status);
        Assert.Equal(SessionCacheIndex.StructureMismatchReason, txt.Reason);
        var empty = EntryFor(result, ".json");
        Assert.Equal(SessionCacheStatus.Unknown, empty.Status);
        Assert.Equal(SessionCacheIndex.StructureMismatchReason, empty.Reason);
    }

    [Fact]
    public void ReparseCacheEntry_IsUnknown_NotFollowed()
    {
        using var fx = FixtureRoot.Create();
        var outside = Path.Combine(fx.Root, "outside-cache");
        Directory.CreateDirectory(outside);
        var link = SessionPath(fx.StoragesRoot, "j-1");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateJunction(link, outside);
        try
        {
            var result = Index.Build(fx.StoragesRoot, new[] { "j-1" });

            var entry = EntryFor(result, "j-1.json");
            Assert.Equal(SessionCacheStatus.Unknown, entry.Status);
            Assert.Equal(SessionCacheIndex.ReparseReason, entry.Reason);
        }
        finally
        {
            Directory.Delete(link, false);
        }
    }

    [Fact]
    public void Build_IsReadOnly_CacheAndWorkspaceJsonBytesUnchanged()
    {
        using var fx = FixtureRoot.Create();
        var cacheBytes = FixtureFactory.Utf8("{\"cache\":true}");
        var cachePath = fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-1.json", cacheBytes);
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-9.json", FixtureFactory.Utf8("{}"));
        var workspacePath = fx.Factory.WriteFile(fx.StoragesRoot, "workspace.json", FixtureFactory.Utf8("{\"ws\":1}"));
        var cacheBefore = File.ReadAllBytes(cachePath);
        var wsBefore = File.ReadAllBytes(workspacePath);

        var result = Index.Build(fx.StoragesRoot, new[] { "s-1" });

        Assert.True(result.RootValid);
        Assert.Equal(cacheBefore, File.ReadAllBytes(cachePath));
        Assert.Equal(wsBefore, File.ReadAllBytes(workspacePath));
        // workspace.json 不在索引范围：任何结果条目都不得指向它。
        Assert.All(result.DedicatedCaches, e => Assert.NotEqual(workspacePath, e.FullPath));
        Assert.All(result.WholeCacheCandidates, e => Assert.NotEqual(workspacePath, e.FullPath));
        Assert.All(result.Anomalies, a => Assert.NotEqual(workspacePath, a.FullPath));
    }

    [Fact]
    public void WholeCacheCandidates_AreListedForExplicitSelection()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteFile(fx.StoragesRoot, $"{SessionsRel}/s-1.json", FixtureFactory.Utf8("{}"));
        var jsonBytes = FixtureFactory.Utf8("{\"whole\":true}");
        var jsonPath = fx.Factory.WriteFile(fx.StoragesRoot, "session_projcache.json", jsonBytes);

        var result = Index.Build(fx.StoragesRoot, new[] { "s-1" });

        var dir = Assert.Single(result.WholeCacheCandidates, e => e.IsDirectory);
        Assert.Equal("session_projcache", dir.RelativePath);
        var json = Assert.Single(result.WholeCacheCandidates, e => !e.IsDirectory);
        Assert.Equal(jsonPath, json.FullPath);
        Assert.Equal("session_projcache.json", json.RelativePath);
        Assert.Equal((long)jsonBytes.Length, json.Length);
    }

    [Fact]
    public void SessionsDirReparse_IsAnomaly_WholeCacheStillListed()
    {
        using var fx = FixtureRoot.Create();
        var outside = Path.Combine(fx.Root, "outside-cache");
        Directory.CreateDirectory(outside);
        var sessionsDir = Path.Combine(fx.StoragesRoot, SessionsRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sessionsDir)!);
        CreateJunction(sessionsDir, outside);
        try
        {
            var result = Index.Build(fx.StoragesRoot, new[] { "s-1" });

            Assert.Empty(result.DedicatedCaches);
            Assert.Contains(result.Anomalies, a => a.Reason == SessionCacheIndex.ReparseReason);
            Assert.Contains(result.WholeCacheCandidates, e => e.IsDirectory);
        }
        finally
        {
            Directory.Delete(sessionsDir, false);
        }
    }

    [Fact]
    public void MissingLayout_ReturnsEmptyResult_NoCrash()
    {
        using var fx = FixtureRoot.Create();

        var result = Index.Build(fx.StoragesRoot, new[] { "s-1" });

        Assert.True(result.RootValid);
        Assert.Empty(result.DedicatedCaches);
        Assert.Empty(result.WholeCacheCandidates);
        Assert.Empty(result.Anomalies);
    }

    [Fact]
    public void InvalidRoot_ReportsRootErrorCode()
    {
        using var fx = FixtureRoot.Create();
        var missing = Path.Combine(fx.Root, "no-such-root");

        var result = Index.Build(missing, new[] { "s-1" });

        Assert.False(result.RootValid);
        Assert.NotNull(result.RootErrorCode);
        Assert.Empty(result.DedicatedCaches);
    }
}
