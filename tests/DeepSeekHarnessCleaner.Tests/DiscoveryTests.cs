using System.Diagnostics;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-08 会话物理候选发现（TC-03）。全部在夹具合成根下，只读断言无写入。</summary>
public sealed class DiscoveryTests
{
    private static readonly SessionLogDiscovery Discovery = new(new PathGuard());

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

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static SortedSet<string> TreeSnapshot(string root)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            set.Add("D:" + Path.GetRelativePath(root, dir));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            set.Add("F:" + Path.GetRelativePath(root, file));
        return set;
    }

    [Fact]
    public void CanonicalNames_AllGenerationsAndEncodings_Recognized()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteSessionFile("proj-a", "s-plain-v0", "session.jsonl", FixtureFactory.Utf8("{}\n"));
        fx.Factory.WriteSessionFile("proj-a", "s-zstd-v0", "session.jsonl.zstd", FixtureFactory.ZstdFrames("{}\n"));
        fx.Factory.WriteSessionFile("proj-b", "s-plain-v7", "session.v7.jsonl", FixtureFactory.Utf8("{}\n"));
        fx.Factory.WriteSessionFile("proj-b", "s-zstd-v4", "session.v4.jsonl.zstd", FixtureFactory.ZstdFrames("{}\n"));

        var result = Discovery.Discover(fx.SessionsRoot);
        Assert.True(result.RootValid);
        Assert.Empty(result.Anomalies);
        Assert.Equal(4, result.Candidates.Count);

        var byName = result.Candidates.ToDictionary(c => c.RelativeDirectory, StringComparer.Ordinal);
        var v0Plain = byName[Path.Combine("proj-a", "s-plain-v0")].Files.Single();
        Assert.Equal(0, v0Plain.Generation);
        Assert.Equal(SessionFileEncoding.Plain, v0Plain.Encoding);
        var v0Zstd = byName[Path.Combine("proj-a", "s-zstd-v0")].Files.Single();
        Assert.Equal(0, v0Zstd.Generation);
        Assert.Equal(SessionFileEncoding.Zstd, v0Zstd.Encoding);
        var v7 = byName[Path.Combine("proj-b", "s-plain-v7")].Files.Single();
        Assert.Equal(7, v7.Generation);
        Assert.Equal(SessionFileEncoding.Plain, v7.Encoding);
        var v4 = byName[Path.Combine("proj-b", "s-zstd-v4")].Files.Single();
        Assert.Equal(4, v4.Generation);
        Assert.Equal(SessionFileEncoding.Zstd, v4.Encoding);
        Assert.Equal(FixtureFactory.Utf8("{}\n").LongLength, v7.Length);
    }

    [Fact]
    public void MultiGeneration_SameDirectory_GroupedAsOneSession()
    {
        // TC-03：v0+v4 同一 ID 合并且保留两个物理文件。
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();

        var result = Discovery.Discover(fx.SessionsRoot);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(2, candidate.Files.Count);
        Assert.False(candidate.IsAmbiguous);
        Assert.Empty(candidate.ConflictingGenerations);
        Assert.Contains(candidate.Files, f => f.Generation == 0 && f.Encoding == SessionFileEncoding.Plain);
        Assert.Contains(candidate.Files, f => f.Generation == 4 && f.Encoding == SessionFileEncoding.Zstd);
    }

    [Fact]
    public void SameGenerationDualEncoding_FlaggedAmbiguous()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteAmbiguousSession();

        var result = Discovery.Discover(fx.SessionsRoot);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.IsAmbiguous);
        Assert.Equal(new[] { 4 }, candidate.ConflictingGenerations);
    }

    [Fact]
    public void NonWhitelistedNames_Flagged_NotCandidates()
    {
        using var fx = FixtureRoot.Create();
        var dir = Path.Combine(fx.SessionsRoot, "proj-x", "s-mixed");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(dir, "session.v0.jsonl"), "{}\n");   // v0 显式代际不在白名单
        File.WriteAllText(Path.Combine(dir, "session.jsonl.bak"), "{}\n");  // 备份
        File.WriteAllText(Path.Combine(dir, "session.vX.jsonl"), "{}\n");   // 不规则
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        // 大写名单外名：NTFS 大小写不敏感，须放独立目录避免与 session.jsonl 同名冲突。
        var upper = Path.Combine(fx.SessionsRoot, "proj-x", "s-upper");
        Directory.CreateDirectory(upper);
        File.WriteAllText(Path.Combine(upper, "Session.jsonl"), "{}\n");

        var result = Discovery.Discover(fx.SessionsRoot);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(Path.Combine("proj-x", "s-mixed"), candidate.RelativeDirectory);
        Assert.Single(candidate.Files);
        var flagged = result.Anomalies.Select(a => a.Reason).ToList();
        Assert.Equal(5, flagged.Count);
        Assert.All(flagged, reason => Assert.Equal(SessionLogDiscovery.NotWhitelistedReason, reason));
    }

    [Fact]
    public void WhitelistedNameDirectlyUnderRoot_FlaggedUnexpectedLocation()
    {
        using var fx = FixtureRoot.Create();
        File.WriteAllText(Path.Combine(fx.SessionsRoot, "session.jsonl"), "{}\n");

        var result = Discovery.Discover(fx.SessionsRoot);
        Assert.Empty(result.Candidates);
        var anomaly = Assert.Single(result.Anomalies);
        Assert.Equal(SessionLogDiscovery.UnexpectedLocationReason, anomaly.Reason);
    }

    [Fact]
    public void JunctionDirectory_FlaggedReparse_NotScanned()
    {
        using var fx = FixtureRoot.Create();
        var outside = Path.Combine(fx.Root, "outside-junction-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "session.jsonl"), "{}\n");
        var link = Path.Combine(fx.SessionsRoot, "proj-junction");
        CreateJunction(link, outside);

        var result = Discovery.Discover(fx.SessionsRoot);
        Assert.Empty(result.Candidates);
        var anomaly = Assert.Single(result.Anomalies);
        Assert.Equal(SessionLogDiscovery.ReparseReason, anomaly.Reason);
        Assert.Equal("proj-junction", anomaly.RelativePath);
        // Directory.Delete(recursive) 删不了 junction（T-07 实测）：Dispose 前先移除链接本身。
        Directory.Delete(link, recursive: false);
    }

    [Fact]
    public void InvalidRoots_ReturnRootError_WithoutScanning()
    {
        using var fx = FixtureRoot.Create();
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(PathGuard.RootInvalidErrorCode, Discovery.Discover(driveRoot).RootErrorCode);
        var missing = Path.Combine(fx.Root, "no-such-root");
        Assert.Equal(PathGuard.RootInvalidErrorCode, Discovery.Discover(missing).RootErrorCode);
        Assert.Equal(PathGuard.RootInvalidErrorCode, Discovery.Discover("").RootErrorCode);
    }

    [Fact]
    public void EmptySessionFile_RecognizedWithZeroLength()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteEmptySession();

        var result = Discovery.Discover(fx.SessionsRoot);
        var file = Assert.Single(result.Candidates).Files.Single();
        Assert.Equal(0, file.Length);
        Assert.Equal(0, file.Generation);
    }

    [Fact]
    public void Discovery_IsReadOnly_TreeAndSentinelUnchanged()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();
        fx.Factory.WriteAmbiguousSession();
        fx.Factory.WriteCorruptJsonSession();
        var sentinel = Path.Combine(fx.Root, "sentinel-outside-sessions.bin");
        File.WriteAllText(sentinel, "sentinel-content");
        var sentinelSha = Sha256(sentinel);
        var before = TreeSnapshot(fx.Root);

        var result = Discovery.Discover(fx.SessionsRoot);
        Assert.True(result.RootValid);

        Assert.Equal(before, TreeSnapshot(fx.Root));
        Assert.Equal(sentinelSha, Sha256(sentinel));
    }
}
