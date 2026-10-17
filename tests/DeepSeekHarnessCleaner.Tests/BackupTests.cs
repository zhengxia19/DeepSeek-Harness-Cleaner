using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-22：原始 ZIP 创建、逐条目 SHA/数量/长度校验、无覆盖发布与失败无副作用（TC-09,TC-20）。</summary>
public sealed class BackupTests
{
    private static PhysicalLog Log(string full, int generation) =>
        new(full, generation, false, new FileInfo(full).Length, DateTimeOffset.UnixEpoch, null, ParseStatus.Valid);

    private static SessionSummary Session(string id, params PhysicalLog[] logs) =>
        new(id, "proj", null, null, DateOrigin.FileMtime, logs, ParseStatus.Valid);

    private static BackupManifest BuildManifest(FixtureRoot fx, string sessionFile, string v4File, string attachmentFile)
    {
        var result = new BackupManifestBuilder().Build(
            fx.DshHome,
            [Session("sess-1", Log(sessionFile, 0), Log(v4File, 4))],
            [attachmentFile]);
        Assert.True(result.Success);
        return result.Manifest!;
    }

    private static (FixtureRoot Fx, BackupManifest Manifest, string BackupDir) Setup(out byte[][] originals)
    {
        var fx = FixtureRoot.Create();
        var session = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.jsonl", FixtureFactory.Utf8("v0 body\n"));
        var v4 = fx.Factory.WriteSessionFile("proj-a", "sess-1", "session.v4.jsonl", FixtureFactory.Utf8("v4 body\n"));
        // 4KB 伪随机合成附件：使单字节破坏偏移稳定落入条目数据流而非头部区。
        var blob = new byte[4096];
        new Random(20261017).NextBytes(blob);
        var attachment = fx.Factory.WriteFile(fx.AttachmentsRoot, "att-1/blob.bin", blob);
        var backupDir = Path.Combine(fx.Root, "backups");
        Directory.CreateDirectory(backupDir);
        originals = [File.ReadAllBytes(session), File.ReadAllBytes(v4), File.ReadAllBytes(attachment)];
        return (fx, BuildManifest(fx, session, v4, attachment), backupDir);
    }

    private static string[] TempLeftovers(string directory) =>
        Directory.GetFiles(directory, "*.tmp-*");

    [Fact]
    public void CreateBackup_PublishesZip_AndExtractedBytesMatchOriginals()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "backup-1.zip");
            var result = new BackupService().CreateBackup(manifest, target);

            Assert.True(result.Success);
            Assert.NotNull(result.Backup);
            Assert.Equal(target, result.Backup!.ZipPath);
            Assert.Equal(BackupManifestBuilder.ComputeSignature(manifest), result.Backup.ManifestSignature);
            Assert.Equal(3, result.Backup.FileEntryCount);
            Assert.Equal(originals.Sum(o => (long)o.Length), result.Backup.TotalBytes);
            Assert.Empty(TempLeftovers(backupDir));

            var extractDir = Path.Combine(fx.Root, "extract");
            ZipFile.ExtractToDirectory(target, extractDir);
            var expected = new[]
            {
                "sessions/proj-a/sess-1/session.jsonl",
                "sessions/proj-a/sess-1/session.v4.jsonl",
                "attachments/att-1/blob.bin",
            };
            for (var i = 0; i < expected.Length; i++)
            {
                var extracted = Path.Combine(extractDir, expected[i].Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(extracted));
                Assert.Equal(originals[i], File.ReadAllBytes(extracted));
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(originals[i])).ToLowerInvariant(),
                    manifest.Entries.Single(e => e.ZipPath == expected[i]).Sha256);
            }

            Assert.True(File.Exists(Path.Combine(extractDir, "manifest.json")));
        }
    }

    [Fact]
    public void CreateBackup_RejectsExistingTarget_WithoutTouchingOldBackup()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "backup-1.zip");
            File.WriteAllText(target, "sentinel-old-backup");

            var result = new BackupService().CreateBackup(manifest, target);

            Assert.False(result.Success);
            Assert.Equal("TARGET_EXISTS", result.ErrorCode);
            Assert.Equal("sentinel-old-backup", File.ReadAllText(target));
            Assert.Empty(TempLeftovers(backupDir));
        }
    }

    [Fact]
    public void CreateBackup_PreCanceledToken_FailsWithoutSideEffects()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "backup-1.zip");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = new BackupService().CreateBackup(manifest, target, cts.Token);

            Assert.False(result.Success);
            Assert.Equal("CANCELED", result.ErrorCode);
            Assert.False(File.Exists(target));
            Assert.Empty(TempLeftovers(backupDir));
        }
    }

    [Fact]
    public void CreateBackup_MissingTargetDirectory_FailsWithoutSideEffects()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "no-such-dir", "backup-1.zip");

            var result = new BackupService().CreateBackup(manifest, target);

            Assert.False(result.Success);
            Assert.Equal("IO_FAILURE", result.ErrorCode);
            Assert.False(Directory.Exists(Path.Combine(backupDir, "no-such-dir")));
        }
    }

    [Fact]
    public void CreateBackup_TargetIsExistingDirectory_FailsWithoutTempLeftover()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "existing-dir");
            Directory.CreateDirectory(target);

            var result = new BackupService().CreateBackup(manifest, target);

            Assert.False(result.Success);
            Assert.Equal("IO_FAILURE", result.ErrorCode);
            Assert.Empty(TempLeftovers(backupDir));
        }
    }

    [Fact]
    public void CreateBackup_DetectsFileChangedAfterManifestBuild_AndRemovesTempZip()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var changed = Path.Combine(fx.SessionsRoot, "proj-a", "sess-1", "session.jsonl");
            File.WriteAllText(changed, "changed after manifest\n");
            var target = Path.Combine(backupDir, "backup-1.zip");

            var result = new BackupService().CreateBackup(manifest, target);

            Assert.False(result.Success);
            Assert.Equal("ZIP_VERIFY_FAILED", result.ErrorCode);
            Assert.False(File.Exists(target));
            Assert.Empty(TempLeftovers(backupDir));
        }
    }

    [Fact]
    public void VerifyZip_PassesOnPublishedZip_AndDetectsSingleByteCorruption()
    {
        byte[][] originals;
        var (fx, manifest, backupDir) = Setup(out originals);
        using (fx)
        {
            var target = Path.Combine(backupDir, "backup-1.zip");
            Assert.True(new BackupService().CreateBackup(manifest, target).Success);
            Assert.True(new BackupService().VerifyZip(target, manifest).Success);

            // SEC-03：人为破坏 zip 单字节后，校验必须失败（三个独立偏移各自验证）。
            var pristine = File.ReadAllBytes(target);
            foreach (var offset in new[] { pristine.Length / 3, pristine.Length / 2, pristine.Length * 2 / 3 })
            {
                var bytes = (byte[])pristine.Clone();
                bytes[offset] ^= 0xFF;
                File.WriteAllBytes(target, bytes);

                var result = new BackupService().VerifyZip(target, manifest);
                Assert.False(result.Success);
                Assert.Equal("ZIP_VERIFY_FAILED", result.ErrorCode);
            }

            File.WriteAllBytes(target, pristine);
            Assert.True(new BackupService().VerifyZip(target, manifest).Success);
        }
    }

    [Fact]
    public void CreateBackup_EmptyManifest_PublishesManifestOnlyZip()
    {
        using var fx = FixtureRoot.Create();
        var backupDir = Path.Combine(fx.Root, "backups");
        Directory.CreateDirectory(backupDir);
        var built = new BackupManifestBuilder().Build(fx.DshHome, []);
        Assert.True(built.Success);

        var target = Path.Combine(backupDir, "empty.zip");
        var result = new BackupService().CreateBackup(built.Manifest!, target);

        Assert.True(result.Success);
        Assert.Equal(0, result.Backup!.FileEntryCount);
        Assert.Equal(0, result.Backup.TotalBytes);
        Assert.True(new BackupService().VerifyZip(target, built.Manifest!).Success);
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(["manifest.json"], zip.Entries.Select(e => e.FullName).ToArray());
    }
}
