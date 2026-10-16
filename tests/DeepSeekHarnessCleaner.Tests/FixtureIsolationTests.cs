using System.IO;
using System.Linq;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;
using ZstdSharp;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-04 验收：夹具可在测试临时根创建/清理，覆盖清单齐备，
/// 且夹具生命周期不触及真实 %USERPROFILE%\.dsh。
/// </summary>
public sealed class FixtureIsolationTests
{
    private static string RealDshPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    private static string SnapshotRealDsh()
    {
        if (!Directory.Exists(RealDshPath))
        {
            return "ABSENT";
        }

        var entries = Directory.EnumerateFiles(RealDshPath, "*", SearchOption.AllDirectories)
            .Select(p =>
            {
                var info = new FileInfo(p);
                return string.Join('|', Path.GetRelativePath(RealDshPath, p), info.Length, info.LastWriteTimeUtc.ToString("O"));
            })
            .OrderBy(x => x, StringComparer.Ordinal);
        return string.Join("\n", entries);
    }

    private static int CountZstdFrames(byte[] bytes)
    {
        var magic = new byte[] { 0x28, 0xB5, 0x2F, 0xFD };
        var count = 0;
        for (var i = 0; i + magic.Length <= bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < magic.Length; j++)
            {
                if (bytes[i + j] != magic[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void Create_LaysOutRootsUnderTemp_NotUnderRealDsh()
    {
        using var fixture = FixtureRoot.Create();
        try
        {
            Assert.StartsWith(Path.GetTempPath(), fixture.Root, StringComparison.Ordinal);
            Assert.False(fixture.Root.StartsWith(RealDshPath, StringComparison.Ordinal));
            Assert.True(Directory.Exists(fixture.SessionsRoot));
            Assert.True(Directory.Exists(fixture.StoragesRoot));
            Assert.True(Directory.Exists(fixture.AttachmentsRoot));
            Assert.True(Directory.Exists(fixture.DesktopHome));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Dispose_RemovesEntireFixtureTree()
    {
        var fixture = FixtureRoot.Create();
        fixture.Factory.WriteValidV4Session();
        var root = fixture.Root;
        Assert.True(Directory.Exists(root));

        fixture.Dispose();
        fixture.Dispose(); // 幂等

        Assert.True(fixture.Disposed);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RealDshRoot_UnchangedByFullFixtureLifecycle()
    {
        var before = SnapshotRealDsh();

        using (var fixture = FixtureRoot.Create())
        {
            BuildFullMatrix(fixture);
        }

        var after = SnapshotRealDsh();
        Assert.Equal(before, after);
    }

    [Fact]
    public void FullMatrix_CoversRequiredFixtureKinds()
    {
        using var fixture = FixtureRoot.Create();
        try
        {
            BuildFullMatrix(fixture);

            // 多版本 + 多代际（TC-03）
            var multiDir = Path.Combine(fixture.SessionsRoot, "proj-alpha", "synth-multi-001");
            Assert.True(File.Exists(Path.Combine(multiDir, "session.jsonl")));
            var multiZstd = File.ReadAllBytes(Path.Combine(multiDir, "session.v4.jsonl.zstd"));
            Assert.Equal(2, CountZstdFrames(multiZstd));
            var plain = new Decompressor().Unwrap(multiZstd);
            Assert.Contains("synthetic frame two", System.Text.Encoding.UTF8.GetString(plain), StringComparison.Ordinal);

            // Ambiguous 同代际并存
            var ambiguousDir = Path.Combine(fixture.SessionsRoot, "proj-beta", "synth-ambiguous-001");
            Assert.True(File.Exists(Path.Combine(ambiguousDir, "session.v4.jsonl")));
            Assert.True(File.Exists(Path.Combine(ambiguousDir, "session.v4.jsonl.zstd")));

            // 无效 JSON（TC-04 Corrupt）
            var corrupt = File.ReadAllText(Path.Combine(fixture.SessionsRoot, "proj-beta", "synth-corrupt-001", "session.v4.jsonl"));
            Assert.Contains(FixtureFactory.CorruptLine, corrupt, StringComparison.Ordinal);

            // 未知事件
            var unknown = File.ReadAllText(Path.Combine(fixture.SessionsRoot, "proj-beta", "synth-unknown-001", "session.v4.jsonl"));
            Assert.Contains(FixtureFactory.UnknownEventLine, unknown, StringComparison.Ordinal);

            // 截断尾帧：不可被当成正常成功解压（TC-04 Partial）
            var truncated = File.ReadAllBytes(Path.Combine(fixture.SessionsRoot, "proj-gamma", "synth-truncated-001", "session.v4.jsonl.zstd"));
            Assert.True(truncated.Length > 0);
            Assert.ThrowsAny<Exception>(() => new Decompressor().Unwrap(truncated));

            // 空文件
            Assert.Equal(0, new FileInfo(Path.Combine(fixture.SessionsRoot, "proj-gamma", "synth-empty-001", "session.jsonl")).Length);

            // 日期夹具（TC-07）
            for (var day = 1; day <= 3; day++)
            {
                var path = Path.Combine(fixture.SessionsRoot, "proj-dates", $"synth-dated-00{day}", "session.v4.jsonl");
                Assert.Equal(new DateTimeOffset(2026, 4, day, 6, 0, 0, TimeSpan.Zero).UtcDateTime, File.GetLastWriteTimeUtc(path));
            }

            // 附件 + 引用（TC-14）
            Assert.True(File.Exists(Path.Combine(fixture.AttachmentsRoot, "att-synth-001.bin")));
            var attachSession = File.ReadAllText(Path.Combine(fixture.SessionsRoot, "proj-attach", "synth-attach-001", "session.v4.jsonl"));
            Assert.Contains("att-synth-001", attachSession, StringComparison.Ordinal);

            // 投影缓存（TC-12）
            Assert.True(File.Exists(Path.Combine(fixture.StoragesRoot, "session_projcache", "sessions", "synth-v4-001.json")));
            Assert.True(File.Exists(Path.Combine(fixture.StoragesRoot, "session_projcache.json")));

            // 安全目录白名单 + 哨兵（TC-13）
            foreach (var dir in FixtureFactory.SafeDesktopDirectories)
            {
                Assert.True(Directory.Exists(Path.Combine(fixture.DesktopHome, dir)));
            }

            Assert.True(File.Exists(Path.Combine(fixture.DesktopHome, FixtureFactory.UnsafeDesktopDirectory, "sentinel.txt")));
            Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(fixture.DesktopHome, "logs")).Count());
        }
        finally
        {
            fixture.Dispose();
            Assert.False(Directory.Exists(fixture.Root));
        }
    }

    private static void BuildFullMatrix(FixtureRoot fixture)
    {
        var factory = fixture.Factory;
        factory.WriteValidV0Session();
        factory.WriteValidV4Session();
        factory.WriteMultiGenerationSession();
        factory.WriteAmbiguousSession();
        factory.WriteCorruptJsonSession();
        factory.WriteUnknownEventSession();
        factory.WriteTruncatedZstdSession();
        factory.WriteEmptySession();
        factory.WriteDatedSessions();
        factory.WriteSessionWithAttachmentReference();
        factory.WriteProjectionCache("synth-v4-001");
        factory.CreateDesktopSafeDirectories();
    }
}
