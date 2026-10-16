using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-10 会话库存与磁盘统计（TC-05）。多代际按经验证身份归并一条逻辑记录；
/// 容量按全部物理文件求和并与 fixture 实际大小相符；单条损坏不中断扫描。
/// </summary>
public sealed class InventoryTests
{
    /// <summary>测试侧适配器：SessionLogDiscovery + SessionHeaderReader → Domain 扫描事实。</summary>
    private sealed class ScanSource : ISessionScanSource
    {
        private readonly SessionLogDiscovery _discovery = new(new PathGuard());
        private readonly SessionHeaderReader _reader = new();

        public SessionScanData Scan(string sessionRoot)
        {
            var discovered = _discovery.Discover(sessionRoot);
            if (!discovered.RootValid)
                return new SessionScanData(discovered.RootErrorCode, [], []);

            var sessions = new List<ScannedSessionDirectory>();
            foreach (var candidate in discovered.Candidates)
            {
                var dirIdentity = Path.GetFileName(candidate.RelativeDirectory);
                var files = new List<ScannedLogFile>();
                foreach (var file in candidate.Files)
                {
                    var header = _reader.Read(file.FullPath, dirIdentity);
                    var log = new PhysicalLog(
                        file.FullPath,
                        file.Generation,
                        file.Encoding == SessionFileEncoding.Zstd,
                        file.Length,
                        SafeModifiedUtc(file.FullPath),
                        Sha256: null,
                        header.Status);
                    files.Add(new ScannedLogFile(
                        log,
                        header.Header?.SessionId,
                        header.Header?.CreatedAt,
                        header.IdentityMatches == false));
                }

                sessions.Add(new ScannedSessionDirectory(candidate.RelativeDirectory, files, candidate.ConflictingGenerations));
            }

            var anomalies = new List<AnomalousFile>();
            foreach (var anomaly in discovered.Anomalies)
            {
                long? bytes = null;
                try
                {
                    if (File.Exists(anomaly.FullPath))
                        bytes = new FileInfo(anomaly.FullPath).Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                anomalies.Add(new AnomalousFile(anomaly.FullPath, anomaly.RelativePath, anomaly.Reason, bytes));
            }

            return new SessionScanData(null, sessions, anomalies);
        }

        private static DateTimeOffset SafeModifiedUtc(string path)
        {
            try
            {
                return new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch (IOException)
            {
                return DateTimeOffset.MinValue;
            }
            catch (UnauthorizedAccessException)
            {
                return DateTimeOffset.MinValue;
            }
        }
    }

    private static InventoryResult ScanInventory(string sessionsRoot) =>
        new InventoryService(new ScanSource()).Scan(sessionsRoot);

    private static long SizeOf(string path) => new FileInfo(path).Length;

    [Fact]
    public void MultiGeneration_MergedIntoSingleLogicalRecord()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();

        var result = ScanInventory(fx.SessionsRoot);

        Assert.Null(result.RootErrorCode);
        Assert.Single(result.Sessions);
        var session = result.Sessions[0];
        Assert.Equal("synth-multi-001", session.SessionId);
        Assert.Equal("proj-alpha", session.Project);
        Assert.Equal(2, session.Generations.Count);
        Assert.Equal([0, 4], session.Generations.Select(g => g.Generation));
        Assert.True(session.Generations[1].Compressed);
        Assert.Equal(ParseStatus.Valid, session.Status);
        Assert.False(session.IsAmbiguous);
        Assert.Equal(new DateTimeOffset(2026, 1, 20, 8, 0, 0, TimeSpan.Zero), session.CreatedAt);
        Assert.Equal(DateOrigin.Header, session.DateOrigin);
        Assert.Null(session.LastActivityAt);
    }

    [Fact]
    public void TotalBytes_SumsAllPhysicalFiles_MatchesFixtureSizes()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteMultiGenerationSession();
        var plain = fx.Factory.WriteValidV4Session();

        var result = ScanInventory(fx.SessionsRoot);

        var multi = result.Sessions.Single(s => s.SessionId == "synth-multi-001");
        var expectedMulti = SizeOf(Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001", "session.jsonl"))
            + SizeOf(Path.Combine(fx.SessionsRoot, "proj-alpha", "synth-multi-001", "session.v4.jsonl.zstd"));
        Assert.Equal(expectedMulti, multi.TotalBytes);
        Assert.Equal(expectedMulti + SizeOf(plain), result.TotalBytes);
    }

    [Fact]
    public void Scan_ContinuesDespiteCorruption_AllSessionsListed()
    {
        using var fx = FixtureRoot.Create();
        var v4 = fx.Factory.WriteValidV4Session();
        var empty = fx.Factory.WriteEmptySession();
        var truncated = fx.Factory.WriteTruncatedZstdSession();
        var corruptJson = fx.Factory.WriteCorruptJsonSession();

        var result = ScanInventory(fx.SessionsRoot);

        // 扫描未中断：四个会话目录全部在册，各自状态可解释。
        Assert.Equal(4, result.Sessions.Count);
        Assert.Equal(ParseStatus.Valid, result.Sessions.Single(s => s.SessionId == "synth-v4-001").Status);
        Assert.Equal(ParseStatus.Corrupt, result.Sessions.Single(s => s.SessionId is null && s.Generations[0].FullPath == empty).Status);
        Assert.Equal(ParseStatus.Partial, result.Sessions.Single(s => s.Generations[0].FullPath == truncated).Status);
        Assert.Equal(ParseStatus.Valid, result.Sessions.Single(s => s.SessionId == "synth-corrupt-001").Status);
        Assert.Equal(
            SizeOf(v4) + SizeOf(empty) + SizeOf(truncated) + SizeOf(corruptJson),
            result.TotalBytes);
    }

    [Fact]
    public void SameGenerationDualEncoding_MarkedAmbiguous()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteAmbiguousSession();

        var result = ScanInventory(fx.SessionsRoot);

        var session = Assert.Single(result.Sessions);
        Assert.True(session.IsAmbiguous);
    }

    [Fact]
    public void HeaderIdentityConflictAcrossGenerations_Ambiguous_SessionIdNull()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteSessionFile("proj-h", "s-conflict", "session.jsonl",
            FixtureFactory.Utf8(FixtureFactory.HeaderLine("id-a", 0, "2026-04-01T00:00:00Z") + "\n"));
        fx.Factory.WriteSessionFile("proj-h", "s-conflict", "session.v4.jsonl.zstd",
            FixtureFactory.ZstdFrames(FixtureFactory.HeaderLine("id-b", 4, "2026-04-02T00:00:00Z") + "\n"));

        var result = ScanInventory(fx.SessionsRoot);

        var session = Assert.Single(result.Sessions);
        Assert.True(session.IsAmbiguous);
        Assert.Null(session.SessionId);
        Assert.Equal(2, session.Generations.Count);
    }

    [Fact]
    public void DirectoryIdentityMismatch_Ambiguous_SessionIdNull()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteSessionFile("proj-x", "s-mismatch", "session.jsonl",
            FixtureFactory.Utf8(FixtureFactory.HeaderLine("other-id", 4, "2026-04-03T00:00:00Z") + "\n"));

        var result = ScanInventory(fx.SessionsRoot);

        var session = Assert.Single(result.Sessions);
        Assert.True(session.IsAmbiguous);
        Assert.Null(session.SessionId);
        Assert.Equal(ParseStatus.Valid, session.Status);
    }

    [Fact]
    public void RootInvalid_ReportsErrorCode_NoScan()
    {
        using var fx = FixtureRoot.Create();
        var missing = Path.Combine(fx.SessionsRoot, "does-not-exist");

        var result = ScanInventory(missing);

        Assert.NotNull(result.RootErrorCode);
        Assert.Empty(result.Sessions);
        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public void AnomalousFiles_ReportedWithReasonAndBytes()
    {
        using var fx = FixtureRoot.Create();
        var stray = fx.Factory.WriteFile(fx.SessionsRoot, "session.jsonl", FixtureFactory.Utf8("stray"));

        var result = ScanInventory(fx.SessionsRoot);

        Assert.Empty(result.Sessions);
        var anomaly = Assert.Single(result.AnomalousFiles);
        Assert.Equal(stray, anomaly.FullPath);
        Assert.Equal(SessionLogDiscovery.UnexpectedLocationReason, anomaly.Reason);
        Assert.Equal(SizeOf(stray), anomaly.Bytes);
    }

    [Fact]
    public async Task ScanAsync_ReturnsSessionsViaResolvedRoots()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteValidV0Session();
        fx.Factory.WriteValidV4Session();

        var probe = new RootProbe(fx.SessionsRoot, RootSource.UserSelected, RootProbeStatus.Valid, null);
        var roots = new ResolvedRoots(
            fx.SessionsRoot, fx.SessionsRoot, fx.StoragesRoot, fx.AttachmentsRoot, fx.DesktopHome, probe, probe);

        var sessions = await new InventoryService(new ScanSource()).ScanAsync(roots, CancellationToken.None);

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void Sorting_CreatedAtDescending_UnknownDatesLast()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteValidV0Session();      // 2026-01-05
        fx.Factory.WriteValidV4Session();      // 2026-02-10
        fx.Factory.WriteEmptySession();        // 无有效 header → 无日期

        var result = ScanInventory(fx.SessionsRoot);

        Assert.Equal(["synth-v4-001", "synth-v0-001"], result.Sessions.Select(s => s.SessionId).Take(2));
        Assert.Null(result.Sessions[2].SessionId);
        Assert.Equal(DateOrigin.Unknown, result.Sessions[2].DateOrigin);
    }
}
