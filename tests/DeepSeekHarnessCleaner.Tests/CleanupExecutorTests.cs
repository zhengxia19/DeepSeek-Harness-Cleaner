using System.IO;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-26：备份门禁 + 删除前复验 + 逐项删除与真实部分结果（TC-11,TC-15,TC-16）。</summary>
public sealed class CleanupExecutorTests
{
    private const string Project = "proj-a";
    private const string SessionId = "sess-1";
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(
        FixtureRoot Fx,
        CleanupPlan Plan,
        BackupManifest Manifest,
        VerifiedBackup Backup,
        HarnessProcessProbe InactiveProbe,
        string SessionFile,
        string V4File,
        string KeepFile,
        string OutsideFile);

    private static PhysicalLog Log(string full, int generation, string? sha = null)
    {
        var info = new FileInfo(full);
        return new PhysicalLog(
            full,
            generation,
            false,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            sha ?? Sha256Of(File.ReadAllBytes(full)),
            ParseStatus.Valid);
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>mklink /J 创建 junction（无需管理员权限），与既有测试同款。</summary>
    private static void CreateJunction(string linkPath, string targetDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDir}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink /J 创建 junction 失败：" + linkPath);
        }
    }

    private static Ctx Setup()
    {
        var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile(Project, SessionId, "session.jsonl", FixtureFactory.Utf8("v0 body\n"), BaseTime);
        var v4 = fx.Factory.WriteSessionFile(Project, SessionId, "session.v4.jsonl", FixtureFactory.Utf8("v4 body\n"), BaseTime);
        var keep = fx.Factory.WriteFile(fx.SessionsRoot, $"{Project}/{SessionId}/keep.txt", FixtureFactory.Utf8("keep\n"), BaseTime);
        var outside = fx.Factory.WriteFile(fx.Root, "outside.txt", FixtureFactory.Utf8("sentinel\n"), BaseTime);

        var sessionSummary = new SessionSummary(
            SessionId, Project, null, null, DateOrigin.FileMtime, [Log(session, 0), Log(v4, 4)], ParseStatus.Valid);

        var plan = new CleanupPlanner().Build(fx.DshHome, [sessionSummary], []).Plan!;
        var manifest = new BackupManifestBuilder().Build(fx.DshHome, [sessionSummary]).Manifest!;

        var backupDir = Path.Combine(fx.Root, "backups");
        Directory.CreateDirectory(backupDir);
        var backupResult = new BackupService().CreateBackup(manifest, Path.Combine(backupDir, "cleanup.zip"));
        Assert.True(backupResult.Success);

        return new Ctx(
            fx, plan, manifest, backupResult.Backup!,
            new HarnessProcessProbe(() => []),
            session, v4, keep, outside);
    }

    private static CleanupExecutionResult Run(
        Ctx ctx,
        bool confirmed = true,
        HarnessProcessProbe? probe = null,
        string? rootOverride = null,
        bool omitBackup = false,
        CancellationToken ct = default) =>
        new CleanupExecutor(probe ?? ctx.InactiveProbe).Execute(
            ctx.Plan, ctx.Manifest, omitBackup ? null : ctx.Backup, rootOverride ?? ctx.Fx.DshHome, confirmed, ct);

    [Fact]
    public void Execute_DeletesPlannedTargets_KeepsUnselectedAndOutsideUntouched()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var keepBefore = File.ReadAllBytes(ctx.KeepFile);
            var outsideBefore = File.ReadAllBytes(ctx.OutsideFile);

            var result = Run(ctx);

            Assert.False(result.Aborted);
            Assert.True(result.AllSucceeded);
            Assert.Equal(2, result.DeletedCount);
            Assert.False(File.Exists(ctx.SessionFile));
            Assert.False(File.Exists(ctx.V4File));
            Assert.Equal(keepBefore, File.ReadAllBytes(ctx.KeepFile));
            Assert.Equal(outsideBefore, File.ReadAllBytes(ctx.OutsideFile));
        }
    }

    [Fact]
    public void Execute_NotConfirmed_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var result = Run(ctx, confirmed: false);

            Assert.True(result.Aborted);
            Assert.Equal(CleanupExecutor.GateNotConfirmedCode, result.AbortCode);
            Assert.Empty(result.Outcomes);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_NullBackup_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var result = Run(ctx, omitBackup: true);

            Assert.True(result.Aborted);
            Assert.Equal(CleanupExecutor.GateBackupRequiredCode, result.AbortCode);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_CorruptZip_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            File.WriteAllBytes(ctx.Backup.ZipPath, new byte[512]);

            var result = Run(ctx);

            Assert.True(result.Aborted);
            Assert.NotNull(result.AbortCode);
            Assert.StartsWith(CleanupExecutor.GateBackupInvalidCode, result.AbortCode, StringComparison.Ordinal);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_MissingZip_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            File.Delete(ctx.Backup.ZipPath);

            var result = Run(ctx);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateBackupInvalidCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_ProcessActive_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var active = new HarnessProcessProbe(() =>
                [new ProcessSnapshot(4242, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]);

            var result = Run(ctx, probe: active);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateProcessCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_ProcessUnknown_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var unknown = new HarnessProcessProbe(() => throw new UnauthorizedAccessException("denied"));

            var result = Run(ctx, probe: unknown);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateProcessCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_FingerprintChanged_AbortsZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var result = Run(ctx, rootOverride: ctx.Fx.DshHome + "-alt");

            Assert.True(result.Aborted);
            Assert.Equal(CleanupExecutor.GateFingerprintCode, result.AbortCode);
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_ChangedLength_SkipsItem_AndDeletesOther()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            File.AppendAllBytes(ctx.SessionFile, FixtureFactory.Utf8("extra\n"));

            var result = Run(ctx);

            Assert.False(result.Aborted);
            Assert.False(result.AllSucceeded);
            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(1, result.SkippedCount);
            var skipped = Assert.Single(result.Outcomes, o => o.Status == ItemStatus.Skipped);
            Assert.Equal("CHANGED_LENGTH", skipped.Reason);
            Assert.True(File.Exists(ctx.SessionFile));
            Assert.False(File.Exists(ctx.V4File));
        }
    }

    [Fact]
    public void Execute_ChangedMtime_SkipsItem()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            File.SetLastWriteTimeUtc(ctx.SessionFile, BaseTime.UtcDateTime.AddSeconds(120));

            var result = Run(ctx);

            var skipped = Assert.Single(result.Outcomes, o => o.Status == ItemStatus.Skipped);
            Assert.Equal("CHANGED_MTIME", skipped.Reason);
            Assert.True(File.Exists(ctx.SessionFile));
        }
    }

    [Fact]
    public void Execute_ShaMismatch_SkipsItem()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            // 计划期望 SHA 与磁盘实际不符（伪造 64 个 0）：长度/mtime 一致仍必须跳过。
            var fakePlan = ctx.Plan with
            {
                Targets = [.. ctx.Plan.Targets.Select(t =>
                    t.AbsolutePath == ctx.SessionFile ? t with { ExpectedSha256 = new string('0', 64) } : t)],
            };

            var result = new CleanupExecutor(ctx.InactiveProbe).Execute(fakePlan, ctx.Manifest, ctx.Backup, ctx.Fx.DshHome, true);

            var skipped = Assert.Single(result.Outcomes, o => o.Status == ItemStatus.Skipped);
            Assert.Equal("CHANGED_SHA256", skipped.Reason);
            Assert.True(File.Exists(ctx.SessionFile));
        }
    }

    [Fact]
    public void Execute_ReadOnlyFile_ReportsFailed_AndDeletesOther()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            File.SetAttributes(ctx.SessionFile, FileAttributes.ReadOnly);
            try
            {
                var result = Run(ctx);

                Assert.False(result.Aborted);
                Assert.Equal(1, result.FailedCount);
                var failed = Assert.Single(result.Outcomes, o => o.Status == ItemStatus.Failed);
                Assert.Equal("PERMISSION_DENIED", failed.Reason);
                Assert.True(File.Exists(ctx.SessionFile));
                Assert.False(File.Exists(ctx.V4File));
            }
            finally
            {
                File.SetAttributes(ctx.SessionFile, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Execute_ReparseJunctionPath_SkipsItem_TargetUntouched()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            var realDir = Path.Combine(ctx.Fx.Root, "realdir");
            Directory.CreateDirectory(realDir);
            var realFile = Path.Combine(realDir, "session.jsonl");
            File.WriteAllBytes(realFile, FixtureFactory.Utf8("through junction\n"));
            File.SetLastWriteTimeUtc(realFile, BaseTime.UtcDateTime);

            var junction = Path.Combine(ctx.Fx.DshHome, "j");
            CreateJunction(junction, realDir);
            try
            {
                var target = new CleanupTarget(
                    Path.Combine(junction, "session.jsonl"),
                    CleanupCategory.SessionLog,
                    SessionId,
                    new FileInfo(realFile).Length,
                    new DateTimeOffset(new FileInfo(realFile).LastWriteTimeUtc, TimeSpan.Zero),
                    Sha256Of(File.ReadAllBytes(realFile)));
                var plan = ctx.Plan with { Targets = [target] };

                var result = new CleanupExecutor(ctx.InactiveProbe).Execute(plan, ctx.Manifest, ctx.Backup, ctx.Fx.DshHome, true);

                var skipped = Assert.Single(result.Outcomes);
                Assert.Equal(ItemStatus.Skipped, skipped.Status);
                Assert.Equal("PATH_REPARSE", skipped.Reason);
                Assert.True(File.Exists(realFile));
            }
            finally
            {
                Directory.Delete(junction, recursive: false);
            }
        }
    }

    [Fact]
    public void Execute_PreCanceled_SkipsAllZeroDeletions()
    {
        var ctx = Setup();
        using (ctx.Fx)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = Run(ctx, ct: cts.Token);

            Assert.False(result.Aborted);
            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(2, result.SkippedCount);
            Assert.All(result.Outcomes, o => Assert.Equal(CleanupExecutor.CanceledCode, o.Reason));
            Assert.True(File.Exists(ctx.SessionFile) && File.Exists(ctx.V4File));
        }
    }
}
