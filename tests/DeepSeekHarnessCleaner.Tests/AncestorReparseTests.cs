using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// B03：祖先 reparse 走查收敛为 PathGuard.FindReparseAncestor 单一实现后，
/// 同一祖先 junction 场景下 7 个入口（RootLocator、SessionResidueInventory、WorkspaceReferenceReader、
/// PostCleanupAuditor、AttachmentOpener、WindowStateStore、WorkspaceBackupService）全部拒绝。
/// </summary>
public sealed class AncestorReparseTests
{
    private static void CreateJunction(string link, string targetDir)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, targetDir }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void SameAncestorJunction_AllSevenEntryPointsReject()
    {
        using var fx = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(fx);
        var workspacePath = fixture.Write("valid");
        File.WriteAllBytes(workspacePath, new byte[] { 0xef, 0xbb, 0xbf }.Concat(File.ReadAllBytes(workspacePath)).Concat(new byte[] { 32, 13, 10 }).ToArray());
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        fx.Factory.WriteFile(fx.AttachmentsRoot, "object", "%PDF-1.7 synthetic"u8.ToArray());
        var link = Path.Combine(fx.Root, "anc-link");
        CreateJunction(link, fx.Root);
        try
        {
            var viaHome = Path.Combine(link, ".dsh");
            var viaSessions = Path.Combine(viaHome, "sessions");

            // 1) RootLocator：候选根祖先含 junction → Invalid + PATH_REPARSE。
            var locator = new RootLocator(folder => folder == Environment.SpecialFolder.UserProfile ? fx.Root : fx.DesktopHome, _ => null);
            var candidate = Assert.Single(locator.DiscoverCandidates(selectedDshHome: viaHome).Candidates,
                x => x.Sources.Contains(RootCandidateSource.UserSelected));
            Assert.Equal(RootCandidateStatus.Invalid, candidate.Status);
            Assert.Equal(PathGuard.PathReparseErrorCode, candidate.Reason);

            // 2) SessionResidueInventory：issues 记 PATH_REPARSE 并提前返回。
            var scan = new SessionResidueInventory().Scan(viaHome, [WorkspaceFixture.ArchivedId], []);
            Assert.Contains(scan.Issues, x => x.Code == PathGuard.PathReparseErrorCode);

            // 3) WorkspaceReferenceReader：Unknown + PATH_REPARSE。
            var reference = new WorkspaceReferenceReader().Read(viaHome);
            Assert.Equal(WorkspaceReferenceStatus.Unknown, reference.Status);
            Assert.Equal(PathGuard.PathReparseErrorCode, reference.Reason);

            // 4) PostCleanupAuditor：计划指纹经 junction 一致，祖先走查是唯一防线。
            var sessions = new InventoryService(new UiScanSource()).Scan(viaSessions).Sessions;
            var plan = new SessionCleanupPreparation().Build(viaSessions, sessions).Plan!;
            var before = new PreCleanupAuditor().Audit(viaSessions, sessions.Select(x => x.SessionId!).ToArray(), plan);
            var post = new PostCleanupAuditor().Audit(viaSessions, plan, before);
            Assert.Null(post.After);
            Assert.Equal(PathGuard.PathReparseErrorCode, Assert.Single(post.Issues).Code);

            // 5) AttachmentOpener：副本根祖先含 junction → 打开失败且不启动外部程序。
            var item = Assert.Single(new AttachmentInventoryService().Scan(viaHome, []).Items);
            var launched = false;
            var opener = new AttachmentOpener(_ => launched = true, Path.Combine(viaHome, "opened"));
            Assert.False(opener.Open(viaHome, item).Success);
            Assert.False(launched);

            // 6) WindowStateStore：记录路径祖先含 junction → 读取不可保存、写入报错。
            var store = new WindowStateStore(Path.Combine(viaHome, "window.json"));
            Assert.False(store.Load().CanSave);
            Assert.NotNull(store.Save(new WindowStateRecord(1, new WindowRectangle(100, 100, 1000, 700), "Normal", 96)));

            // 7) WorkspaceBackupService：备份根祖先含 junction → CheckRoot 拒绝（计划经真实路径构建）。
            var backupPlan = BuildProposedPlan(fx);
            var service = new WorkspaceBackupService(new HarnessProcessProbe(() => []));
            var backupRoot = Path.Combine(link, ".dsh", "metadata-backups");
            Assert.Equal("WORKSPACE_BACKUP_CREATE_FAILED", service.Create(backupPlan, backupRoot).ErrorCode);
            Assert.Equal("WORKSPACE_BACKUP_VERIFY_FAILED", service.Verify(backupPlan, backupRoot).ErrorCode);
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private static WorkspaceMetadataPlan BuildProposedPlan(FixtureRoot root)
    {
        var fixture = new WorkspaceFixture(root);
        var path = fixture.Write("valid");
        File.WriteAllBytes(path, new byte[] { 0xef, 0xbb, 0xbf }.Concat(File.ReadAllBytes(path)).Concat(new byte[] { 32, 13, 10 }).ToArray());
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var plan = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), plan);
        var execution = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(plan, null, null, root.SessionsRoot, true);
        Assert.False(execution.Aborted);
        var facts = execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath,
            x.Status == ItemStatus.Deleted ? WorkspacePhysicalStatus.Deleted : WorkspacePhysicalStatus.Failed)).ToArray();
        var proposal = new WorkspaceMetadataProposal().Build(plan, before, facts, new PostCleanupAuditor().Audit(root.SessionsRoot, plan, before));
        Assert.Equal(WorkspaceSyncState.Proposed, proposal.State);
        return proposal;
    }
}
