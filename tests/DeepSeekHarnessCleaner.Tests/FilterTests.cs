using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-11 会话日期与搜索筛选（TC-07/TC-08）。跨月/跨年/不同时区偏移、start=end、
/// 日期为空断言一致；筛选只按完整会话取舍，不裁剪事件内容。
/// </summary>
public sealed class FilterTests
{
    private static SessionSummary Make(
        string? sessionId,
        string? project,
        DateTimeOffset? createdAt,
        DateTimeOffset? modified,
        int generations = 1)
    {
        var logs = new List<PhysicalLog>();
        for (var i = 0; i < generations; i++)
        {
            logs.Add(new PhysicalLog(
                $@"C:\fixture\{project}\{sessionId}\session{(i == 0 ? "" : $".v{4 * i}")}.jsonl",
                4 * i,
                Compressed: i > 0,
                100 + i,
                modified ?? DateTimeOffset.MinValue,
                Sha256: null,
                ParseStatus.Valid));
        }

        return new SessionSummary(
            sessionId,
            project,
            createdAt,
            LastActivityAt: null,
            createdAt is null ? DateOrigin.Unknown : DateOrigin.Header,
            logs,
            ParseStatus.Valid)
        {
            TotalBytes = logs.Sum(l => l.Bytes),
        };
    }

    private static IReadOnlyList<SessionSummary> Apply(
        IReadOnlyList<SessionSummary> sessions,
        FilterRequest request,
        TimeZoneInfo zone) => SessionFilter.Apply(sessions, request, zone);

    private static FilterRequest ByDate(DateFilterField field, string start, string end) =>
        new(new DateFilter(field, DateOnly.Parse(start), DateOnly.Parse(end)), null, null, null);

    [Fact]
    public void CreatedAt_CrossYear_ConsistentAcrossTimezones()
    {
        // 2025-12-31T20:00Z：+08:00 本地为 2026-01-01（跨年），UTC 本地为 2025-12-31。
        var session = Make("s-xyear", "proj-a", new DateTimeOffset(2025, 12, 31, 20, 0, 0, TimeSpan.Zero), null);
        var sessions = new[] { session };
        var cst = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

        Assert.Single(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-01-01", "2026-01-01"), cst));
        Assert.Empty(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2025-12-31", "2025-12-31"), cst));
        Assert.Single(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2025-12-31", "2025-12-31"), TimeZoneInfo.Utc));
        Assert.Empty(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-01-01", "2026-01-01"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void StartEqualsEnd_MatchesSingleLocalDay()
    {
        var hit = Make("s-day", "proj-a", null, new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        var miss = Make("s-edge", "proj-a", null, new DateTimeOffset(2026, 3, 16, 0, 30, 0, TimeSpan.Zero));
        var sessions = new[] { hit, miss };

        var result = Apply(sessions, ByDate(DateFilterField.FileModified, "2026-03-15", "2026-03-15"), TimeZoneInfo.Utc);

        Assert.Equal(["s-day"], result.Select(s => s.SessionId));
    }

    [Fact]
    public void UnknownDates_Excluded_NotCoercedToToday()
    {
        var noCreated = Make("s-nc", "proj-a", null, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var noActivity = Make("s-na", "proj-a", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null);
        var noFiles = new SessionSummary("s-nf", "proj-a", null, null, DateOrigin.Unknown, [], ParseStatus.Corrupt);

        Assert.Empty(Apply(new[] { noCreated }, ByDate(DateFilterField.CreatedAt, "2020-01-01", "2030-12-31"), TimeZoneInfo.Utc));
        Assert.Empty(Apply(new[] { noActivity }, ByDate(DateFilterField.LastActivityAt, "2020-01-01", "2030-12-31"), TimeZoneInfo.Utc));
        Assert.Empty(Apply(new[] { noFiles }, ByDate(DateFilterField.FileModified, "2020-01-01", "2030-12-31"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void SameInstant_DifferentOffsets_FallInDifferentLocalDays()
    {
        var instant = new DateTimeOffset(2026, 6, 30, 13, 0, 0, TimeSpan.Zero);
        var session = Make("s-off", "proj-a", instant, null);
        var sessions = new[] { session };
        var plus14 = TimeZoneInfo.CreateCustomTimeZone("TEST+14", TimeSpan.FromHours(14), "test +14", "test +14");
        var minus11 = TimeZoneInfo.CreateCustomTimeZone("TEST-11", TimeSpan.FromHours(-11), "test -11", "test -11");

        // +14 → 2026-07-01；-11 → 2026-06-30。
        Assert.Single(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-07-01", "2026-07-01"), plus14));
        Assert.Empty(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-07-01", "2026-07-01"), minus11));
        Assert.Single(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-06-30", "2026-06-30"), minus11));
        Assert.Empty(Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-06-30", "2026-06-30"), plus14));
    }

    [Fact]
    public void ProjectFilter_IsCaseInsensitiveContains()
    {
        var sessions = new[] { Make("s1", "proj-alpha", null, null), Make("s2", "proj-beta", null, null) };

        var result = Apply(sessions, new FilterRequest(null, "PROJ-Alpha", null, null), TimeZoneInfo.Utc);

        Assert.Equal(["s1"], result.Select(s => s.SessionId));
        Assert.Equal(["s1"], Apply(sessions, new FilterRequest(null, "alpha", null, null), TimeZoneInfo.Utc).Select(s => s.SessionId));
    }

    [Fact]
    public void SessionIdFilter_IsCaseInsensitiveContains()
    {
        var sessions = new[] { Make("synth-v4-001", "p", null, null), Make("synth-v4-0011", "p", null, null) };

        var result = Apply(sessions, new FilterRequest(null, null, "SYNTH-V4-001", null), TimeZoneInfo.Utc);

        Assert.Equal(2, result.Count);
        Assert.Equal(["synth-v4-001", "synth-v4-0011"], result.Select(s => s.SessionId));
    }

    [Fact]
    public void Keyword_MatchesProjectAndSessionId_CaseInsensitive()
    {
        var sessions = new[]
        {
            Make("s-1", "proj-gamma", null, null),
            Make("synth-key-7", "proj-delta", null, null),
            Make("s-2", "proj-epsilon", null, null),
        };

        var result = Apply(sessions, new FilterRequest(null, null, null, "KEY"), TimeZoneInfo.Utc);
        Assert.Equal(["synth-key-7"], result.Select(s => s.SessionId));

        result = Apply(sessions, new FilterRequest(null, null, null, "gamma"), TimeZoneInfo.Utc);
        Assert.Equal(["s-1"], result.Select(s => s.SessionId));
    }

    [Fact]
    public void TitleFilter_AndKeywordMatchPersistedTitle()
    {
        var named = Make("session-1", "proj-a", null, null) with { Title = "My important discussion" };
        var unnamed = Make("session-2", "proj-a", null, null);
        Assert.Same(named, Assert.Single(Apply([named, unnamed],
            new FilterRequest(null, null, null, null, "important"), TimeZoneInfo.Utc)));
        Assert.Same(named, Assert.Single(Apply([named, unnamed],
            new FilterRequest(null, null, null, "DISCUSSION"), TimeZoneInfo.Utc)));
    }

    [Fact]
    public void CombinedFilters_AreAnded()
    {
        var inRange = Make("synth-a", "proj-alpha", new DateTimeOffset(2026, 4, 2, 6, 0, 0, TimeSpan.Zero), null);
        var outOfRange = Make("synth-b", "proj-alpha", new DateTimeOffset(2026, 4, 9, 6, 0, 0, TimeSpan.Zero), null);
        var wrongProject = Make("synth-c", "proj-beta", new DateTimeOffset(2026, 4, 2, 6, 0, 0, TimeSpan.Zero), null);
        var sessions = new[] { inRange, outOfRange, wrongProject };

        var request = new FilterRequest(
            new DateFilter(DateFilterField.CreatedAt, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 3)),
            "proj-alpha",
            null,
            "synth");

        var result = Apply(sessions, request, TimeZoneInfo.Utc);

        Assert.Equal(["synth-a"], result.Select(s => s.SessionId));
    }

    [Fact]
    public void Filter_KeepsWholeSessionsIntact_NoEventTrimming()
    {
        var multi = Make("synth-multi", "proj-alpha", new DateTimeOffset(2026, 4, 2, 6, 0, 0, TimeSpan.Zero), null, generations: 2);
        var other = Make("synth-other", "proj-beta", new DateTimeOffset(2026, 4, 2, 6, 0, 0, TimeSpan.Zero), null);

        var result = Apply(new[] { multi, other }, new FilterRequest(null, "proj-alpha", null, null), TimeZoneInfo.Utc);

        // TC-08：筛选/选择只以完整会话为单位，不裁剪事件内容。
        var kept = Assert.Single(result);
        Assert.Same(multi, kept);
        Assert.Equal(2, kept.Generations.Count);
        Assert.Equal(multi.TotalBytes, kept.TotalBytes);
    }

    [Fact]
    public void StartAfterEnd_Throws()
    {
        var request = ByDate(DateFilterField.CreatedAt, "2026-04-05", "2026-04-01");
        Assert.Throws<ArgumentException>(() => SessionFilter.Apply([], request, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Integration_DatedFixtureSessions_FilterByFileModifiedAndCreatedAt()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteDatedSessions();

        var sessions = new InventoryService(new ScanSource()).Scan(fx.SessionsRoot).Sessions;
        Assert.Equal(3, sessions.Count);

        var modified = Apply(sessions, ByDate(DateFilterField.FileModified, "2026-04-02", "2026-04-03"), TimeZoneInfo.Utc);
        // 库存按日期降序，筛选保持该顺序：003 → 002。
        Assert.Equal(["synth-dated-003", "synth-dated-002"], modified.Select(s => s.SessionId));

        var created = Apply(sessions, ByDate(DateFilterField.CreatedAt, "2026-04-01", "2026-04-01"), TimeZoneInfo.Utc);
        Assert.Equal(["synth-dated-001"], created.Select(s => s.SessionId));
    }

    /// <summary>最小扫描源：仅用于集成用例，把 fixture 会话目录映射为 Domain 扫描事实。</summary>
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
                        new DateTimeOffset(new FileInfo(file.FullPath).LastWriteTimeUtc, TimeSpan.Zero),
                        Sha256: null,
                        header.Status);
                    files.Add(new ScannedLogFile(log, header.Header?.SessionId, header.Header?.CreatedAt, header.IdentityMatches == false));
                }

                sessions.Add(new ScannedSessionDirectory(candidate.RelativeDirectory, files, candidate.ConflictingGenerations));
            }

            return new SessionScanData(null, sessions, []);
        }
    }
}
