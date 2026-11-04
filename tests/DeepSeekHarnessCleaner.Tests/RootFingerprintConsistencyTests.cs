using DeepSeekHarnessCleaner.Application;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// S02 验收：同一根经各规划器与审计器（RootGuard 单一实现）所得 RootFingerprint 一致；
/// 根带尾分隔符与不带时指纹相同（原 CleanupExecutor 拷贝不做 TrimEnd，会误判 ROOT_CHANGED）。
/// </summary>
public sealed class RootFingerprintConsistencyTests : IDisposable
{
    private readonly string _root;

    public RootFingerprintConsistencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "s02-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
    }

    [Fact]
    public void SameRoot_SameFingerprint_AcrossAllPlanners()
    {
        var sessionPlan = new CleanupPlanner().Build(_root, [], []).Plan!;
        var cachePlan = new CacheCleanupSelection()
            .Select(_root, CacheCleanupMode.WholeCache, [], []).Plan!;
        var desktopPlan = new DesktopCleanupSelection().Select(_root, []).Plan!;
        var attachmentPlan = new AttachmentCleanupSelection().Select(_root, [], []).Plan!;

        Assert.Equal(sessionPlan.RootFingerprint, cachePlan.RootFingerprint);
        Assert.Equal(sessionPlan.RootFingerprint, desktopPlan.RootFingerprint);
        Assert.Equal(sessionPlan.RootFingerprint, attachmentPlan.RootFingerprint);
        // 审计器与预言机替换点使用的方法与规划器同一实现。
        Assert.Equal(sessionPlan.RootFingerprint, RootGuard.RootFingerprint(_root));
    }

    [Fact]
    public void TrailingSeparator_Root_YieldsSameFingerprint()
    {
        var plain = new CleanupPlanner().Build(_root, [], []).Plan!.RootFingerprint;
        var trailing = new CleanupPlanner()
            .Build(_root + Path.DirectorySeparatorChar, [], []).Plan!.RootFingerprint;
        Assert.Equal(plain, trailing);
        // 执行器门径（TryRootFingerprint）与计划指纹对带尾分隔符根同样匹配。
        Assert.Equal(plain, RootGuard.TryRootFingerprint(_root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DifferentRoots_DifferentFingerprints()
    {
        var other = Path.Combine(_root, "sessions");
        Assert.NotEqual(
            new CleanupPlanner().Build(_root, [], []).Plan!.RootFingerprint,
            new CleanupPlanner().Build(other, [], []).Plan!.RootFingerprint);
    }

    [Fact]
    public void InvalidRoot_TryRootFingerprintReturnsNull_AndPlannersReject()
    {
        Assert.Null(RootGuard.TryRootFingerprint("C:\\"));
        Assert.Equal("ROOT_INVALID", new CleanupPlanner().Build("C:\\", [], []).RootErrorCode);
        Assert.Equal("ROOT_INVALID", new CacheCleanupSelection()
            .Select("C:\\", CacheCleanupMode.WholeCache, [], []).RootErrorCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
