using System.IO;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-28：桌面缓存/日志清理计划（TC-13）：仅 5 个 cache 目录 + logs；日志按修改日期筛选；Local Storage/Session Storage/Network 恒不变；误选根被拒绝。</summary>
public sealed class DesktopCleanupTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Root = @"C:\synth\desktop-root\dsh-desktop";

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static DesktopFileEvidence Evidence(string full, DateTimeOffset? mtime = null) =>
        new(full, 10, mtime ?? BaseTime, null);

    private static DesktopCleanupPlanResult Select(
        IReadOnlyList<DesktopFileEvidence> candidates,
        DateTimeOffset? logCutoff = null,
        string root = Root) =>
        new DesktopCleanupSelection().Select(root, candidates, logCutoff);

    [Fact]
    public void OnlyFiveWhitelistedCacheDirs_Included_OtherDomainsExcluded()
    {
        var result = Select(
            [
                Evidence(P("Cache", "a.dat")),
                Evidence(P("Code Cache", "b.dat")),
                Evidence(P("GPUCache", "c.dat")),
                Evidence(P("DawnGraphiteCache", "d.dat")),
                Evidence(P("DawnWebGPUCache", "e.dat")),
                Evidence(P("Local Storage", "f.dat")),
                Evidence(P("Session Storage", "g.dat")),
                Evidence(P("Network", "h.dat")),
            ]);

        var plan = result.Plan!;
        Assert.Equal(5, plan.Targets.Count);
        Assert.All(plan.Targets, t => Assert.Equal(CleanupCategory.DesktopCache, t.Category));
        var excluded = result.Exclusions.Where(e => e.Reason == DesktopCleanupSelection.OutOfScopeReason).ToList();
        Assert.Equal(3, excluded.Count);
        Assert.Contains(excluded, e => e.FullPath == P("Local Storage", "f.dat"));
        Assert.Contains(excluded, e => e.FullPath == P("Session Storage", "g.dat"));
        Assert.Contains(excluded, e => e.FullPath == P("Network", "h.dat"));
    }

    [Fact]
    public void NestedFilesInsideCacheDirs_Included()
    {
        var result = Select([Evidence(P("Cache", "sub", "deep", "x.dat"))]);

        Assert.Single(result.Plan!.Targets);
    }

    [Fact]
    public void Logs_IncludedAsDesktopLog_DateFilterSelectsOlderOnly()
    {
        var old = Evidence(P("logs", "a.log"), new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var recent = Evidence(P("logs", "b.log"), new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));

        var all = Select([old, recent]);
        var filtered = Select([old, recent], logCutoff: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, all.Plan!.Targets.Count);
        Assert.All(all.Plan!.Targets, t => Assert.Equal(CleanupCategory.DesktopLog, t.Category));
        var kept = Assert.Single(filtered.Plan!.Targets);
        Assert.Equal(P("logs", "a.log"), kept.AbsolutePath);
        Assert.Equal(DesktopCleanupSelection.LogDateFilteredReason, Assert.Single(filtered.Exclusions).Reason);
    }

    [Fact]
    public void DateFilter_DoesNotAffectCacheTargets()
    {
        var oldCache = Evidence(P("Cache", "old.dat"), new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = Select([oldCache], logCutoff: new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(result.Plan!.Targets);
        Assert.Empty(result.Exclusions);
    }

    [Fact]
    public void RootItselfAndTopLevelEntries_Rejected()
    {
        var result = Select(
            [
                Evidence(Root),                    // 误选根自身
                Evidence(P("Cache")),              // 白名单目录自身
                Evidence(P("stray.txt")),          // 根下散落文件
            ]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(DesktopCleanupSelection.RootNotTargetReason, result.Exclusions[0].Reason);
        Assert.Equal(DesktopCleanupSelection.TopLevelNotTargetReason, result.Exclusions[1].Reason);
        Assert.Equal(DesktopCleanupSelection.TopLevelNotTargetReason, result.Exclusions[2].Reason);
    }

    [Fact]
    public void OutOfRootAndTraversalExcluded()
    {
        var result = Select(
            [
                Evidence(@"C:\synth\elsewhere\Cache\a.dat"),
                Evidence($@"{Root}\..\escape\Cache\b.dat"),
            ]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("DESKTOP_CROSS_ROOT", result.Exclusions[0].Reason);
        Assert.Equal("DESKTOP_PATH_TRAVERSAL", result.Exclusions[1].Reason);
    }

    [Fact]
    public void RootInvalid_Rejected_ForBlankAndDriveRoot()
    {
        var blank = Select([], root: "   ");
        var drive = Select([], root: @"C:\");

        Assert.Equal("ROOT_INVALID", blank.RootErrorCode);
        Assert.Equal("ROOT_INVALID", drive.RootErrorCode);
    }

    [Fact]
    public void DuplicatePathExcluded_SecondOccurrence()
    {
        var dup = Evidence(P("Cache", "a.dat"));
        var result = Select([dup, dup]);

        Assert.Single(result.Plan!.Targets);
        Assert.Equal(DesktopCleanupSelection.DuplicateReason, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void Plan_HasFingerprint_NoBackup_SortedTargets()
    {
        var a1 = Select([Evidence(P("Cache", "z.dat")), Evidence(P("Cache", "a.dat"))]);
        var a2 = Select([]);
        var b = Select([], root: @"C:\synth\desktop-root-B");

        var plan = a1.Plan!;
        Assert.Matches("^[0-9a-f]{64}$", plan.RootFingerprint);
        Assert.False(plan.RequiresBackup);
        Assert.Equal(a1.Plan!.RootFingerprint, a2.Plan!.RootFingerprint);
        Assert.NotEqual(a1.Plan!.RootFingerprint, b.Plan!.RootFingerprint);
        Assert.Equal([P("Cache", "a.dat"), P("Cache", "z.dat")], plan.Targets.Select(t => t.AbsolutePath));
    }

    // ---- 同一安全 executor 通道：无会话 ZIP；进程门禁；受保护域哈希不变 ----

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static (FixtureRoot Fx, string Desktop) SetupDesktop()
    {
        var fx = FixtureRoot.Create();
        var desktop = Path.Combine(fx.Root, "dsh-desktop");
        fx.Factory.WriteFile(desktop, "Cache/x.dat", FixtureFactory.Utf8("cache-bytes"), BaseTime);
        fx.Factory.WriteFile(desktop, "logs/y.log", FixtureFactory.Utf8("log-bytes\n"), BaseTime);
        fx.Factory.WriteFile(desktop, "Local Storage/state.dat", FixtureFactory.Utf8("ls-bytes"), BaseTime);
        fx.Factory.WriteFile(desktop, "Session Storage/s.dat", FixtureFactory.Utf8("ss-bytes"), BaseTime);
        fx.Factory.WriteFile(desktop, "Network/n.dat", FixtureFactory.Utf8("net-bytes"), BaseTime);
        return (fx, desktop);
    }

    private static IReadOnlyList<DesktopFileEvidence> EvidenceFrom(FixtureRoot fx, string desktop, params string[] relatives) =>
        relatives
            .Select(r =>
            {
                var full = Path.Combine(desktop, r);
                var info = new FileInfo(full);
                return new DesktopFileEvidence(full, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Sha256Of(full));
            })
            .ToList();

    [Fact]
    public void Executor_CacheAndLogsCombination_DeletesTargets_ProtectedDomainsHashUnchanged()
    {
        var (fx, desktop) = SetupDesktop();
        using (fx)
        {
            var lsBefore = Sha256Of(Path.Combine(desktop, "Local Storage", "state.dat"));
            var ssBefore = Sha256Of(Path.Combine(desktop, "Session Storage", "s.dat"));
            var netBefore = Sha256Of(Path.Combine(desktop, "Network", "n.dat"));
            var plan = new DesktopCleanupSelection().Select(
                desktop,
                EvidenceFrom(fx, desktop, "Cache/x.dat", "logs/y.log", "Local Storage/state.dat", "Session Storage/s.dat", "Network/n.dat")).Plan!;
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(plan, null, null, desktop, true);

            Assert.False(result.Aborted);
            Assert.True(result.AllSucceeded);
            Assert.Equal(2, result.DeletedCount);
            Assert.False(File.Exists(Path.Combine(desktop, "Cache", "x.dat")));
            Assert.False(File.Exists(Path.Combine(desktop, "logs", "y.log")));
            Assert.Equal(lsBefore, Sha256Of(Path.Combine(desktop, "Local Storage", "state.dat")));
            Assert.Equal(ssBefore, Sha256Of(Path.Combine(desktop, "Session Storage", "s.dat")));
            Assert.Equal(netBefore, Sha256Of(Path.Combine(desktop, "Network", "n.dat")));
        }
    }

    [Fact]
    public void Executor_LogsOnlyCombination_CacheUntouched_ProtectedDomainsHashUnchanged()
    {
        var (fx, desktop) = SetupDesktop();
        using (fx)
        {
            var cacheBefore = Sha256Of(Path.Combine(desktop, "Cache", "x.dat"));
            var lsBefore = Sha256Of(Path.Combine(desktop, "Local Storage", "state.dat"));
            var plan = new DesktopCleanupSelection().Select(
                desktop,
                EvidenceFrom(fx, desktop, "logs/y.log", "Local Storage/state.dat")).Plan!;
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(plan, null, null, desktop, true);

            Assert.False(result.Aborted);
            Assert.Equal(1, result.DeletedCount);
            Assert.False(File.Exists(Path.Combine(desktop, "logs", "y.log")));
            Assert.Equal(cacheBefore, Sha256Of(Path.Combine(desktop, "Cache", "x.dat")));
            Assert.Equal(lsBefore, Sha256Of(Path.Combine(desktop, "Local Storage", "state.dat")));
        }
    }

    [Fact]
    public void Executor_ProcessActive_Blocked_NothingDeleted()
    {
        var (fx, desktop) = SetupDesktop();
        using (fx)
        {
            var plan = new DesktopCleanupSelection().Select(
                desktop,
                EvidenceFrom(fx, desktop, "Cache/x.dat", "logs/y.log")).Plan!;
            var active = new HarnessProcessProbe(() =>
                [new ProcessSnapshot(88, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]);

            var result = new CleanupExecutor(active).Execute(plan, null, null, desktop, true);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateProcessCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(desktop, "Cache", "x.dat")));
            Assert.True(File.Exists(Path.Combine(desktop, "logs", "y.log")));
        }
    }
}
