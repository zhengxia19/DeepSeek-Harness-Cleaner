using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-24：会话精确清理计划（TC-08/TC-11）：逐文件计划含全部代际与专属缓存；未选/异常/冲突/未知/根外不进入；计划带指纹。</summary>
public sealed class CleanupPlanTests
{
    private const string Root = @"C:\synth\plan-root\sessions";

    private static PhysicalLog Log(string dir, string fileName, int gen, long bytes = 100) =>
        new(
            $@"{Root}\{dir}\{fileName}",
            gen,
            fileName.EndsWith(".zstd", StringComparison.Ordinal),
            bytes,
            new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
            null,
            ParseStatus.Valid);

    private static SessionSummary Session(string? id, ParseStatus status = ParseStatus.Valid, bool ambiguous = false, params PhysicalLog[] logs) =>
        new(
            id,
            "proj",
            new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
            null,
            DateOrigin.Header,
            logs,
            status)
        { TotalBytes = logs.Sum(static l => l.Bytes), IsAmbiguous = ambiguous };

    private static CleanupPlanResult Build(IReadOnlyList<SessionSummary> selected, IReadOnlyList<DedicatedCacheEvidence>? caches = null, string root = Root) =>
        new CleanupPlanner().Build(root, selected, caches ?? []);

    [Fact]
    public void Plan_SelectedSessionIncludesAllGenerationsOnly()
    {
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0), Log("dir-a", "session.v4.jsonl", 4)]);
        var result = Build([a]);

        Assert.True(result.RootValid);
        Assert.NotNull(result.Plan);
        Assert.Empty(result.Exclusions);
        Assert.Equal(2, result.Plan!.Targets.Count);
        Assert.All(result.Plan.Targets, t =>
        {
            Assert.Equal(CleanupCategory.SessionLog, t.Category);
            Assert.Equal("synth-a", t.OwnerSessionId);
            Assert.Equal(100, t.ExpectedLength);
        });
        Assert.Contains(result.Plan.Targets, t => t.AbsolutePath.EndsWith("session.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Plan.Targets, t => t.AbsolutePath.EndsWith("session.v4.jsonl", StringComparison.Ordinal));
        Assert.False(result.Plan.RequiresBackup);
    }

    [Fact]
    public void Plan_UnselectedSessionsNeverEnterPlan()
    {
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0)]);
        // 未选会话根本不传入选择集：计划只含所选会话文件（TC-08 选择即整会话）。
        var result = Build([a]);

        Assert.Single(result.Plan!.Targets);
        Assert.Equal($@"{Root}\dir-a\session.jsonl", result.Plan.Targets[0].AbsolutePath);
    }

    [Fact]
    public void Plan_DateFilterSelectionProducesWholeSessionsOnly()
    {
        // 日期筛选只产生完整会话选择（T-11 输出 SessionSummary），计划含其全部代际、无会话内部拆分。
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0), Log("dir-a", "session.v2.jsonl.zstd", 2)]);
        var result = Build([a]);

        Assert.Equal(2, result.Plan!.Targets.Count);
        Assert.Equal(2, result.Plan.Targets.Count(t => t.OwnerSessionId == "synth-a"));
        Assert.DoesNotContain(result.Plan.Targets, t => t.OwnerSessionId is null);
    }

    [Fact]
    public void Plan_AbnormalFormatExcluded()
    {
        var corrupt = Session("synth-c", status: ParseStatus.Corrupt, logs: [Log("dir-c", "session.jsonl", 0)]);
        var result = Build([corrupt]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Single(result.Exclusions);
        Assert.Equal("ABNORMAL_FORMAT", result.Exclusions[0].Reason);
        Assert.False(result.Plan.RequiresBackup);
    }

    [Fact]
    public void Plan_AbnormalGenerationExcludesWholeSession()
    {
        var mixed = Session("synth-m", logs:
        [
            Log("dir-m", "session.jsonl", 0),
            new($@"{Root}\dir-m\session.v4.jsonl", 4, false, 50, DateTimeOffset.UnixEpoch, null, ParseStatus.Unreadable),
        ]);
        var result = Build([mixed]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("ABNORMAL_GENERATION", result.Exclusions[0].Reason);
    }

    [Fact]
    public void Plan_AmbiguousSessionExcluded()
    {
        var ambiguous = Session("synth-x", ambiguous: true, logs: [Log("dir-x", "session.jsonl", 0)]);
        var result = Build([ambiguous]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("AMBIGUOUS", result.Exclusions[0].Reason);
    }

    [Fact]
    public void Plan_DuplicateSessionIdExcludesBoth()
    {
        var one = Session("synth-dup", logs: [Log("dir-d1", "session.jsonl", 0)]);
        var other = Session("synth-dup", logs: [Log("dir-d2", "session.jsonl", 0)]);
        var result = Build([one, other]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(2, result.Exclusions.Count);
        Assert.All(result.Exclusions, e => Assert.Equal("DUPLICATE_SESSION_ID", e.Reason));
    }

    [Fact]
    public void Plan_UnverifiedSessionIdExcluded()
    {
        var noId = Session(null, logs: [Log("dir-n", "session.jsonl", 0)]);
        var result = Build([noId]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("UNVERIFIED_SESSION_ID", result.Exclusions[0].Reason);
    }

    [Fact]
    public void Plan_SharedFileBetweenSessionsExcludesBoth()
    {
        var shared = Log("dir-s", "session.jsonl", 0);
        var one = Session("synth-s1", logs: [shared]);
        var other = Session("synth-s2", logs: [shared]);
        var result = Build([one, other]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(2, result.Exclusions.Count);
        Assert.All(result.Exclusions, e => Assert.Equal("SHARED_FILE", e.Reason));
    }

    [Fact]
    public void Plan_UnknownFileNamesExcluded()
    {
        var odd = Session("synth-u", logs: [Log("dir-u", "session.bak", 0)]);
        var vZero = Session("synth-v0", logs: [Log("dir-v0", "session.v0.jsonl", 0)]);
        var upper = Session("synth-up", logs: [Log("dir-up", "Session.JSONL", 0)]);
        var result = Build([odd, vZero, upper]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(3, result.Exclusions.Count);
        Assert.All(result.Exclusions, e => Assert.Equal("UNKNOWN_FILE", e.Reason));
    }

    [Fact]
    public void Plan_OutsideRootAndTraversalExcluded()
    {
        var outside = Session("synth-out", logs:
        [
            new(@"C:\synth\outside\session.jsonl", 0, false, 10, DateTimeOffset.UnixEpoch, null, ParseStatus.Valid),
        ]);
        var traversal = Session("synth-tr", logs:
        [
            new($@"{Root}\..\escape\session.jsonl", 0, false, 10, DateTimeOffset.UnixEpoch, null, ParseStatus.Valid),
        ]);
        var result = Build([outside, traversal]);

        Assert.Empty(result.Plan!.Targets);
        Assert.Contains(result.Exclusions, e => e.Reason == "CROSS_ROOT");
        Assert.Contains(result.Exclusions, e => e.Reason == "PATH_TRAVERSAL");
    }

    [Fact]
    public void Plan_DedicatedCacheOnlyForSelectedPlannedSessions()
    {
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0)]);
        var corrupt = Session("synth-c", status: ParseStatus.Partial, logs: [Log("dir-c", "session.jsonl", 0)]);
        var caches = new DedicatedCacheEvidence[]
        {
            new($@"{Root}\session_projcache\sessions\synth-a.json", "synth-a", 42, DateTimeOffset.UnixEpoch, null),
            new($@"{Root}\session_projcache\sessions\synth-b.json", "synth-b", 7, DateTimeOffset.UnixEpoch, null),
            new($@"{Root}\session_projcache\sessions\synth-c.json", "synth-c", 9, DateTimeOffset.UnixEpoch, null),
        };

        var result = Build([a, corrupt], caches);

        Assert.Single(result.Plan!.Targets, static t => t.Category == CleanupCategory.DedicatedCache);
        var cache = result.Plan.Targets.Single(t => t.Category == CleanupCategory.DedicatedCache);
        Assert.Equal("synth-a", cache.OwnerSessionId);
        Assert.Equal(42, cache.ExpectedLength);
        Assert.DoesNotContain("synth-b", result.Plan.Targets.Select(t => t.OwnerSessionId));
        Assert.DoesNotContain("synth-c", result.Plan.Targets.Select(t => t.OwnerSessionId));
    }

    [Fact]
    public void Plan_CacheOutsideRootExcluded()
    {
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0)]);
        var caches = new DedicatedCacheEvidence[]
        {
            new(@"C:\synth\outside\synth-a.json", "synth-a", 1, DateTimeOffset.UnixEpoch, null),
        };

        var result = Build([a], caches);

        Assert.Single(result.Plan!.Targets); // 仅会话日志
        Assert.Equal("CACHE_CROSS_ROOT", result.Exclusions[0].Reason);
    }

    [Fact]
    public void Plan_FingerprintStablePerRootAndDistinctAcrossRoots()
    {
        var a = Session("synth-a", logs: [Log("dir-a", "session.jsonl", 0)]);
        var first = Build([a]);
        var second = Build([a]);
        var otherRoot = Build([a], root: @"C:\synth\other-root\sessions");

        Assert.Matches("^[0-9a-f]{64}$", first.Plan!.RootFingerprint);
        Assert.Equal(first.Plan!.RootFingerprint, second.Plan!.RootFingerprint);
        Assert.NotEqual(first.Plan!.RootFingerprint, otherRoot.Plan!.RootFingerprint);
        Assert.NotEqual(first.Plan.PlanId, second.Plan!.PlanId);
    }

    [Fact]
    public void Plan_InvalidRootReturnsError()
    {
        var result = Build([], root: "   ");

        Assert.False(result.RootValid);
        Assert.Equal("ROOT_INVALID", result.RootErrorCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Plan_EmptySelectionProducesEmptyPlanWithoutBackup()
    {
        var result = Build([]);

        Assert.True(result.RootValid);
        Assert.Empty(result.Plan!.Targets);
        Assert.False(result.Plan.RequiresBackup);
    }
}
