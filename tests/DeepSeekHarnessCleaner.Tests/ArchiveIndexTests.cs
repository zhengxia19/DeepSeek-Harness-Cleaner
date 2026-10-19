using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// N-01 归档标志只读索引（SessionArchiveIndex）与库存接线（SessionSummary.IsArchived）。
/// 正/负 fixture：缺失、非对象根、非数组、含未知 id、超限、reparse；归档 id 按 header id 精确匹配；
/// 全程只读：workspace.json 字节与 mtime 不变（D5）。
/// </summary>
public sealed class ArchiveIndexTests
{
    private static void CreateJunction(string linkPath, string targetDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process!.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("mklink /J 创建 junction 失败：" + linkPath);
    }

    private static void WriteWorkspace(FixtureRoot fx, string json) =>
        File.WriteAllText(Path.Combine(fx.StoragesRoot, SessionArchiveIndex.WorkspaceFileName), json);

    /// <summary>测试侧全栈适配器：发现 + 头部读取 + 归档索引（与 UiScanSource 同构）。</summary>
    private sealed class FullScanSource : ISessionScanSource
    {
        private readonly SessionLogDiscovery _discovery = new(new PathGuard());
        private readonly SessionHeaderReader _reader = new();
        private readonly SessionArchiveIndex _archiveIndex = new();

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
                        new FileInfo(file.FullPath).LastWriteTimeUtc,
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

            var archive = _archiveIndex.Read(sessionRoot);
            return new SessionScanData(null, sessions, [])
            {
                ArchivedSessionIds = archive.ArchivedSessionIds,
                ArchiveStatus = archive.Status,
            };
        }
    }

    /// <summary>固定扫描事实源：直接验证 MergeSession 的归档匹配语义。</summary>
    private sealed class FixedSource(SessionScanData data) : ISessionScanSource
    {
        public SessionScanData Scan(string sessionRoot) => data;
    }

    private static ScannedSessionDirectory SingleFileDir(string sessionId) =>
        new(
            "proj-alpha/" + sessionId,
            [
                new ScannedLogFile(
                    new PhysicalLog(
                        @"C:\fixture\proj-alpha\" + sessionId + @"\session.jsonl",
                        Generation: 0,
                        Compressed: false,
                        Bytes: 100,
                        ModifiedAt: DateTimeOffset.UnixEpoch,
                        Sha256: null,
                        ParseStatus.Valid),
                    HeaderSessionId: sessionId,
                    HeaderCreatedAt: DateTimeOffset.UnixEpoch,
                    IdentityMismatch: false),
            ],
            []);

    [Fact]
    public void Read_ValidFile_ParsesArchivedIds()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """
            {"unit":{"name":"workspace","version":2},
             "global":{"initialized":true,"archivedSessionIds":["session-a","session-b"],"pinnedSessionIds":[]},
             "tables":{"workspaces":[]}}
            """);

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Null(result.Status);
        Assert.True(result.IsAvailable);
        Assert.Equal(2, result.ArchivedSessionIds.Count);
        Assert.Contains("session-a", result.ArchivedSessionIds);
        Assert.Contains("session-b", result.ArchivedSessionIds);
    }

    [Fact]
    public void Read_MissingFile_EmptySetWithMissingStatus()
    {
        using var fx = FixtureRoot.Create();

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusMissing, result.Status);
    }

    [Fact]
    public void Read_NonObjectRoot_StructureUnknown()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, "[1,2,3]");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusStructureUnknown, result.Status);
    }

    [Fact]
    public void Read_GlobalNotObject_StructureUnknown()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """{"global":42}""");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusStructureUnknown, result.Status);
    }

    [Fact]
    public void Read_IdsNotArray_StructureUnknown()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """{"global":{"archivedSessionIds":"session-a"}}""");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusStructureUnknown, result.Status);
    }

    [Fact]
    public void Read_NonStringElement_StructureUnknown()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """{"global":{"archivedSessionIds":["session-a",7]}}""");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusStructureUnknown, result.Status);
    }

    [Fact]
    public void Read_GlobalOrIdsMissing_AvailableWithEmptySet()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """{"unit":{"name":"workspace","version":2},"tables":{}}""");

        var noGlobal = new SessionArchiveIndex().Read(fx.SessionsRoot);
        Assert.Null(noGlobal.Status);
        Assert.Empty(noGlobal.ArchivedSessionIds);

        WriteWorkspace(fx, """{"global":{"initialized":true}}""");
        var noIds = new SessionArchiveIndex().Read(fx.SessionsRoot);
        Assert.Null(noIds.Status);
        Assert.Empty(noIds.ArchivedSessionIds);
    }

    [Fact]
    public void Read_JsonInvalid_JsonInvalidStatus()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, "{\"global\":{\"archivedSessionIds\":[\"a\",");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusJsonInvalid, result.Status);
    }

    [Fact]
    public void Read_OverSizeLimit_TooLargeStatus()
    {
        using var fx = FixtureRoot.Create();
        var padding = new string('x', (int)SessionArchiveIndex.MaxWorkspaceBytes + 1024);
        WriteWorkspace(fx, $$"""{"global":{"archivedSessionIds":[]},"pad":"{{padding}}"}""");

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Empty(result.ArchivedSessionIds);
        Assert.Equal(SessionArchiveIndex.StatusTooLarge, result.Status);
    }

    [Fact]
    public void Read_JunctionWorkspace_ReparseStatus()
    {
        using var fx = FixtureRoot.Create();
        var target = Path.Combine(fx.Root, "ws-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, SessionArchiveIndex.WorkspaceFileName),
            """{"global":{"archivedSessionIds":["session-a"]}}""");
        CreateJunction(Path.Combine(fx.StoragesRoot, SessionArchiveIndex.WorkspaceFileName), target);
        try
        {
            var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

            Assert.Empty(result.ArchivedSessionIds);
            Assert.Equal(SessionArchiveIndex.StatusReparse, result.Status);
        }
        finally
        {
            // 先删除 junction 链接本身（不跟随），避免 fixture 递归清理进入目标目录。
            Directory.Delete(Path.Combine(fx.StoragesRoot, SessionArchiveIndex.WorkspaceFileName));
        }
    }

    [Fact]
    public void Read_IsReadOnly_WorkspaceBytesAndMtimeUnchanged()
    {
        using var fx = FixtureRoot.Create();
        WriteWorkspace(fx, """{"global":{"archivedSessionIds":["session-a"]}}""");
        var path = Path.Combine(fx.StoragesRoot, SessionArchiveIndex.WorkspaceFileName);
        var before = File.ReadAllBytes(path);
        var beforeMtime = new FileInfo(path).LastWriteTimeUtc;

        var result = new SessionArchiveIndex().Read(fx.SessionsRoot);

        Assert.Null(result.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(beforeMtime, new FileInfo(path).LastWriteTimeUtc);
    }

    [Fact]
    public void FullStack_ArchivedIdMatchesHeaderId_SessionIsArchived()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteValidV0Session(sessionId: "synth-v0-001");
        fx.Factory.WriteValidV4Session(project: "proj-beta", sessionId: "synth-v4-002");
        // 含未知 id：不匹配任何会话，容忍且不影响其余会话。
        WriteWorkspace(fx, """{"global":{"archivedSessionIds":["synth-v4-002","session-unknown-9999"]}}""");

        var result = new InventoryService(new FullScanSource()).Scan(fx.SessionsRoot);

        Assert.Null(result.RootErrorCode);
        Assert.Null(result.ArchiveStatus);
        Assert.Equal(2, result.Sessions.Count);
        var archived = result.Sessions.Single(static s => s.SessionId == "synth-v4-002");
        var plain = result.Sessions.Single(static s => s.SessionId == "synth-v0-001");
        Assert.True(archived.IsArchived);
        Assert.False(plain.IsArchived);
    }

    [Fact]
    public void FullStack_WorkspaceMissing_ScanSucceedsAllUnarchivedWithStatus()
    {
        using var fx = FixtureRoot.Create();
        fx.Factory.WriteValidV0Session(sessionId: "synth-v0-001");

        var result = new InventoryService(new FullScanSource()).Scan(fx.SessionsRoot);

        Assert.Null(result.RootErrorCode);
        Assert.Equal(SessionArchiveIndex.StatusMissing, result.ArchiveStatus);
        Assert.All(result.Sessions, static s => Assert.False(s.IsArchived));
    }

    [Fact]
    public void MergeSession_ArchivedMatchingIsByHeaderId_UnknownIdNeverArchived()
    {
        var dir = SingleFileDir("synth-x-001");
        var data = new SessionScanData(null, [dir], [])
        {
            ArchivedSessionIds = new HashSet<string>(StringComparer.Ordinal) { "synth-x-001" },
        };
        var hit = new InventoryService(new FixedSource(data)).Scan(@"C:\fixture");
        Assert.True(hit.Sessions.Single().IsArchived);

        var missData = new SessionScanData(null, [dir], [])
        {
            ArchivedSessionIds = new HashSet<string>(StringComparer.Ordinal) { "synth-other" },
        };
        var miss = new InventoryService(new FixedSource(missData)).Scan(@"C:\fixture");
        Assert.False(miss.Sessions.Single().IsArchived);
    }

    [Fact]
    public void MergeSession_UnknownSessionId_NeverArchived()
    {
        // header 身份不匹配 → SessionId=null → 即使归档集合含目录名也不标记。
        var dir = new ScannedSessionDirectory(
            "proj-alpha/synth-y-001",
            [
                new ScannedLogFile(
                    new PhysicalLog(
                        @"C:\fixture\proj-alpha\synth-y-001\session.jsonl",
                        Generation: 0,
                        Compressed: false,
                        Bytes: 100,
                        ModifiedAt: DateTimeOffset.UnixEpoch,
                        Sha256: null,
                        ParseStatus.Valid),
                    HeaderSessionId: "synth-y-001",
                    HeaderCreatedAt: DateTimeOffset.UnixEpoch,
                    IdentityMismatch: true),
            ],
            []);
        var data = new SessionScanData(null, [dir], [])
        {
            ArchivedSessionIds = new HashSet<string>(StringComparer.Ordinal) { "synth-y-001" },
        };

        var result = new InventoryService(new FixedSource(data)).Scan(@"C:\fixture");

        var session = result.Sessions.Single();
        Assert.Null(session.SessionId);
        Assert.False(session.IsArchived);
    }

    [Fact]
    public void ScanData_Defaults_AvailableEmptySet_BackwardCompatible()
    {
        var data = new SessionScanData(null, [], []);
        Assert.Empty(data.ArchivedSessionIds);
        Assert.Null(data.ArchiveStatus);
    }
}
