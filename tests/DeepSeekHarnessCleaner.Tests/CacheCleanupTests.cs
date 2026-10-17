using System.IO;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-27：投影缓存独立清理计划（TC-12）：单会话精确匹配、全量显式选择、workspace.json 永不列入、同一 executor 通道。</summary>
public sealed class CacheCleanupTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Root = @"C:\synth\cache-root\storages";

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static CacheFileEvidence Evidence(string full, string? owner = null) =>
        new(full, owner, 10, BaseTime, null);

    private static CacheCleanupPlanResult Select(
        CacheCleanupMode mode,
        IReadOnlyList<string> selected,
        IReadOnlyList<CacheFileEvidence> candidates,
        string root = Root) =>
        new CacheCleanupSelection().Select(root, mode, selected, candidates);

    [Fact]
    public void SingleSession_OnlyExactMatchedSelectedSession_EntersPlan()
    {
        var result = Select(
            CacheCleanupMode.SingleSession,
            ["sess-1"],
            [
                Evidence(P("session_projcache", "sessions", "sess-1.json"), "sess-1"),
                Evidence(P("session_projcache", "sessions", "sess-2.json"), "sess-2"),
            ]);

        Assert.True(result.RootValid);
        var plan = result.Plan!;
        Assert.Single(plan.Targets);
        Assert.Equal(P("session_projcache", "sessions", "sess-1.json"), plan.Targets[0].AbsolutePath);
        Assert.Equal(CleanupCategory.DedicatedCache, plan.Targets[0].Category);
        Assert.False(plan.RequiresBackup);
    }

    [Fact]
    public void SingleSession_StructureMismatchExcluded()
    {
        var result = Select(
            CacheCleanupMode.SingleSession,
            ["sess-1"],
            [
                // 文件名与会话 ID 不精确一致。
                Evidence(P("session_projcache", "sessions", "other.json"), "sess-1"),
                // 位置不在 sessions/ 子目录。
                Evidence(P("session_projcache", "sess-1.json"), "sess-1"),
            ]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(2, result.Exclusions.Count);
        Assert.All(result.Exclusions, e => Assert.Equal(CacheCleanupSelection.StructureMismatchReason, e.Reason));
    }

    [Fact]
    public void SingleSession_UnselectedOrOwnerlessExcluded()
    {
        var result = Select(
            CacheCleanupMode.SingleSession,
            ["sess-1"],
            [
                Evidence(P("session_projcache", "sessions", "sess-9.json"), "sess-9"),
                Evidence(P("session_projcache", "sessions", "sess-1.json"), null),
            ]);

        Assert.Empty(result.Plan!.Targets);
        Assert.All(result.Exclusions, e => Assert.Equal(CacheCleanupSelection.NotSelectedReason, e.Reason));
    }

    [Fact]
    public void WorkspaceJson_NeverListedInEitherMode()
    {
        var single = Select(CacheCleanupMode.SingleSession, ["sess-1"], [Evidence(P("workspace.json"), "sess-1")]);
        var whole = Select(CacheCleanupMode.WholeCache, [], [Evidence(P("workspace.json"), "sess-1")]);

        Assert.Empty(single.Plan!.Targets);
        Assert.Empty(whole.Plan!.Targets);
        Assert.Equal(CacheCleanupSelection.WorkspaceReason, single.Exclusions[0].Reason);
        Assert.Equal(CacheCleanupSelection.WorkspaceReason, whole.Exclusions[0].Reason);
    }

    [Fact]
    public void WholeCache_IncludesCacheScope_ExcludesOtherDomains()
    {
        var result = Select(
            CacheCleanupMode.WholeCache,
            [],
            [
                Evidence(P("session_projcache", "sessions", "a.json")),
                Evidence(P("session_projcache", "sub", "nested.bin")),
                Evidence(P("session_projcache.json")),
                Evidence(P("workspace.json")),
                Evidence(P("other.json")),
                Evidence(P("Local Storage", "x.dat")),
            ]);

        var plan = result.Plan!;
        Assert.Equal(3, plan.Targets.Count);
        Assert.Contains(plan.Targets, t => t.AbsolutePath == P("session_projcache.json"));
        Assert.Contains(plan.Targets, t => t.AbsolutePath == P("session_projcache", "sub", "nested.bin"));
        var outOfScope = result.Exclusions.Where(e => e.Reason == CacheCleanupSelection.OutOfScopeReason).ToList();
        Assert.Equal(2, outOfScope.Count);
        Assert.Contains(result.Exclusions, e => e.Reason == CacheCleanupSelection.WorkspaceReason);
    }

    [Fact]
    public void WholeCache_RequiresExplicitMode_NeverImplicit()
    {
        var candidates = new[]
        {
            Evidence(P("session_projcache", "sessions", "a.json")),
            Evidence(P("session_projcache.json")),
        };

        var singleEmptySelection = Select(CacheCleanupMode.SingleSession, [], candidates);

        Assert.Empty(singleEmptySelection.Plan!.Targets);
        Assert.Equal(2, singleEmptySelection.Exclusions.Count);
    }

    [Fact]
    public void OutOfRootAndTraversalExcluded()
    {
        var result = Select(
            CacheCleanupMode.WholeCache,
            [],
            [
                Evidence(@"C:\synth\elsewhere\session_projcache.json"),
                Evidence($@"{Root}\..\escape\sess-1.json"),
            ]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("CACHE_CROSS_ROOT", result.Exclusions[0].Reason);
        Assert.Equal("CACHE_PATH_TRAVERSAL", result.Exclusions[1].Reason);
    }

    [Fact]
    public void DuplicatePathExcluded_SecondOccurrence()
    {
        var dup = Evidence(P("session_projcache.json"));
        var result = Select(CacheCleanupMode.WholeCache, [], [dup, dup]);

        Assert.Single(result.Plan!.Targets);
        Assert.Equal(CacheCleanupSelection.DuplicateReason, result.Exclusions[0].Reason);
    }

    [Fact]
    public void RootInvalid_ReturnsErrorCode()
    {
        var result = Select(CacheCleanupMode.WholeCache, [], [], root: "   ");

        Assert.False(result.RootValid);
        Assert.Equal("ROOT_INVALID", result.RootErrorCode);
    }

    [Fact]
    public void Fingerprint_StablePerRoot_DistinctAcrossRoots()
    {
        var a1 = Select(CacheCleanupMode.WholeCache, [], []);
        var a2 = Select(CacheCleanupMode.WholeCache, [], []);
        var b = Select(CacheCleanupMode.WholeCache, [], [], root: @"C:\synth\cache-root-B\storages");

        Assert.Matches("^[0-9a-f]{64}$", a1.Plan!.RootFingerprint);
        Assert.Equal(a1.Plan!.RootFingerprint, a2.Plan!.RootFingerprint);
        Assert.NotEqual(a1.Plan!.RootFingerprint, b.Plan!.RootFingerprint);
    }

    // ---- 同一安全 executor 通道（T-26）：缓存删除不要求会话 ZIP；进程/路径门禁仍生效 ----

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (FixtureRoot Fx, CleanupPlan Plan, string CacheFile, string Workspace, string Other) SetupCache()
    {
        var fx = FixtureRoot.Create();
        var cache = fx.Factory.WriteFile(fx.StoragesRoot, "session_projcache/sessions/sess-1.json", FixtureFactory.Utf8("{\"cache\":1}\n"), BaseTime);
        var workspace = fx.Factory.WriteFile(fx.StoragesRoot, "workspace.json", FixtureFactory.Utf8("{\"ws\":1}\n"), BaseTime);
        var other = fx.Factory.WriteFile(fx.StoragesRoot, "other.json", FixtureFactory.Utf8("{\"other\":1}\n"), BaseTime);

        var evidence = new[]
        {
            new CacheFileEvidence(cache, "sess-1", new FileInfo(cache).Length,
                new DateTimeOffset(new FileInfo(cache).LastWriteTimeUtc, TimeSpan.Zero), Sha256Of(File.ReadAllBytes(cache))),
        };
        var plan = new CacheCleanupSelection().Select(fx.StoragesRoot, CacheCleanupMode.SingleSession, ["sess-1"], evidence).Plan!;
        return (fx, plan, cache, workspace, other);
    }

    [Fact]
    public void Executor_CachePlanWithoutZip_DeletesOnlyMatched_KeepsWorkspaceAndOther()
    {
        var (fx, plan, cache, workspace, other) = SetupCache();
        using (fx)
        {
            var wsBefore = File.ReadAllBytes(workspace);
            var otherBefore = File.ReadAllBytes(other);
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(plan, null, null, fx.StoragesRoot, true);

            Assert.False(result.Aborted);
            Assert.True(result.AllSucceeded);
            Assert.False(File.Exists(cache));
            Assert.Equal(wsBefore, File.ReadAllBytes(workspace));
            Assert.Equal(otherBefore, File.ReadAllBytes(other));
        }
    }

    [Fact]
    public void Executor_CachePlanProcessActive_StillBlocked()
    {
        var (fx, plan, cache, _, _) = SetupCache();
        using (fx)
        {
            var active = new HarnessProcessProbe(() =>
                [new ProcessSnapshot(77, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]);

            var result = new CleanupExecutor(active).Execute(plan, null, null, fx.StoragesRoot, true);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateProcessCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(cache));
        }
    }

    [Fact]
    public void Executor_BackupRequiredPlanWithoutZip_StillGated()
    {
        var (fx, plan, cache, _, _) = SetupCache();
        using (fx)
        {
            var sessionLike = plan with { RequiresBackup = true };
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(sessionLike, null, null, fx.StoragesRoot, true);

            Assert.True(result.Aborted);
            Assert.Equal(CleanupExecutor.GateBackupRequiredCode, result.AbortCode);
            Assert.True(File.Exists(cache));
        }
    }

    [Fact]
    public void Executor_CacheTargetOutsideRoot_SkippedByPathGuard()
    {
        var (fx, plan, _, workspace, _) = SetupCache();
        using (fx)
        {
            var outsidePath = Path.Combine(fx.Root, "outside-cache.json");
            File.WriteAllText(outsidePath, "x");
            var outsideTarget = new CleanupTarget(
                outsidePath,
                CleanupCategory.DedicatedCache,
                "sess-1",
                0,
                BaseTime,
                null);
            var unsafePlan = plan with { Targets = [outsideTarget] };
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(unsafePlan, null, null, fx.StoragesRoot, true);

            var skipped = Assert.Single(result.Outcomes);
            Assert.Equal(ItemStatus.Skipped, skipped.Status);
            Assert.Equal(PathGuard.PathEscapeErrorCode, skipped.Reason);
            Assert.True(File.Exists(workspace));
        }
    }
}
