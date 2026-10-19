using System.IO;
using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>T-29：显式附件备份与独立清理（TC-14）：人工选择、Unknown 明确标注、共享不自动加入、未备份不删除、非普通文件拒绝。</summary>
public sealed class AttachmentCleanupTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Root = @"C:\synth\att-root\.dsh";

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static AttachmentCandidate Candidate(
        string full,
        AttachmentEvidenceStatus status = AttachmentEvidenceStatus.ConfirmedUnreferenced,
        bool regular = true) =>
        new(full, status, regular, 10, BaseTime, null);

    private static AttachmentCleanupPlanResult Select(
        IReadOnlyList<AttachmentCandidate> candidates,
        IReadOnlyList<string> selected,
        string root = Root) =>
        new AttachmentCleanupSelection().Select(root, candidates, selected);

    [Fact]
    public void OnlyExplicitlySelectedRegularFiles_EnterPlan()
    {
        var result = Select(
            [
                Candidate(P("attachments", "a1.png"), AttachmentEvidenceStatus.Referenced),
                Candidate(P("attachments", "a2.png")),
                Candidate(P("attachments", "sub", "a3.png"), AttachmentEvidenceStatus.Unknown),
            ],
            [P("attachments", "a1.png"), P("attachments", "sub", "a3.png")]);

        var plan = result.Plan!;
        Assert.Equal(2, plan.Targets.Count);
        Assert.All(plan.Targets, t => Assert.Equal(CleanupCategory.Attachment, t.Category));
        Assert.False(plan.RequiresBackup); // N-04（D1）：附件删除备份可选。
        Assert.Equal(AttachmentCleanupSelection.NotSelectedReason, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void ReferencedOrSharedAttachments_NeverAutoAdded()
    {
        var result = Select(
            [
                Candidate(P("attachments", "shared.png"), AttachmentEvidenceStatus.Referenced),
                Candidate(P("attachments", "orphan.png"), AttachmentEvidenceStatus.ConfirmedUnreferenced),
            ],
            []);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal(2, result.Exclusions.Count);
        Assert.All(result.Exclusions, e => Assert.Equal(AttachmentCleanupSelection.NotSelectedReason, e.Reason));
    }

    [Fact]
    public void UnknownEvidenceStatus_MarkedExplicitly()
    {
        var result = Select(
            [
                Candidate(P("attachments", "u1.png"), AttachmentEvidenceStatus.Unknown),
                Candidate(P("attachments", "u2.png"), AttachmentEvidenceStatus.Unknown),
            ],
            [P("attachments", "u1.png")]);

        Assert.Single(result.Plan!.Targets);
        Assert.Equal(2, result.Statuses.Count);
        Assert.All(result.Statuses, s => Assert.Equal(AttachmentEvidenceStatus.Unknown, s.Status));
    }

    [Fact]
    public void NonRegularFiles_Rejected()
    {
        var result = Select(
            [
                Candidate(P("attachments", "dir-attachment"), regular: false),
                Candidate(P("attachments", "link.png"), regular: false),
            ],
            [P("attachments", "dir-attachment"), P("attachments", "link.png")]);

        Assert.Empty(result.Plan!.Targets);
        Assert.All(result.Exclusions, e => Assert.Equal(AttachmentCleanupSelection.NotRegularFileReason, e.Reason));
        Assert.Empty(result.Statuses);
    }

    [Fact]
    public void OutsideAttachmentsDir_Excluded()
    {
        var result = Select(
            [
                Candidate(P("sessions", "proj", "sess-1.jsonl")),
                Candidate(P("storages", "other.json")),
                Candidate(P("attachments")),
            ],
            [P("sessions", "proj", "sess-1.jsonl"), P("storages", "other.json"), P("attachments")]);

        Assert.Empty(result.Plan!.Targets);
        Assert.All(result.Exclusions, e => Assert.Equal(AttachmentCleanupSelection.OutOfScopeReason, e.Reason));
    }

    [Fact]
    public void OutOfRootAndTraversalExcluded()
    {
        var result = Select(
            [
                Candidate(@"C:\synth\elsewhere\attachments\a.png"),
                Candidate($@"{Root}\..\escape\attachments\b.png"),
            ],
            []);

        Assert.Empty(result.Plan!.Targets);
        Assert.Equal("ATTACHMENT_CROSS_ROOT", result.Exclusions[0].Reason);
        Assert.Equal("ATTACHMENT_PATH_TRAVERSAL", result.Exclusions[1].Reason);
    }

    [Fact]
    public void RootInvalid_Rejected_ForBlankAndDriveRoot()
    {
        var blank = Select([], [], root: "   ");
        var drive = Select([], [], root: @"C:\");

        Assert.Equal("ROOT_INVALID", blank.RootErrorCode);
        Assert.Equal("ROOT_INVALID", drive.RootErrorCode);
    }

    [Fact]
    public void DuplicatePathExcluded_SecondOccurrence()
    {
        var dup = Candidate(P("attachments", "a.png"));
        var result = Select([dup, dup], [P("attachments", "a.png")]);

        Assert.Single(result.Plan!.Targets);
        Assert.Equal(AttachmentCleanupSelection.DuplicateReason, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void Plan_HasFingerprint_NoBackupRequirementViolation_SortedTargets()
    {
        var a1 = Select(
            [Candidate(P("attachments", "z.png")), Candidate(P("attachments", "a.png"))],
            [P("attachments", "z.png"), P("attachments", "a.png")]);
        var a2 = Select([], []);
        var b = Select([], [], root: @"C:\synth\att-root-B\.dsh");

        var plan = a1.Plan!;
        Assert.Matches("^[0-9a-f]{64}$", plan.RootFingerprint);
        Assert.False(plan.RequiresBackup); // N-04（D1）：附件删除备份可选。
        Assert.Equal(a1.Plan!.RootFingerprint, a2.Plan!.RootFingerprint);
        Assert.NotEqual(a1.Plan!.RootFingerprint, b.Plan!.RootFingerprint);
        Assert.Equal([P("attachments", "a.png"), P("attachments", "z.png")], plan.Targets.Select(t => t.AbsolutePath));
    }

    // ---- 同一安全 executor 通道（N-04/D1）：备份可选、确认必需；提供备份仍校验 ----

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static (FixtureRoot Fx, CleanupPlan Plan, string Target, string Keep) SetupAttachments(bool selectTarget = true)
    {
        var fx = FixtureRoot.Create();
        var target = fx.Factory.WriteFile(fx.AttachmentsRoot, "a1.png", FixtureFactory.Utf8("png-bytes"), BaseTime);
        var keep = fx.Factory.WriteFile(fx.AttachmentsRoot, "keep.png", FixtureFactory.Utf8("keep-bytes"), BaseTime);

        var candidate = new AttachmentCandidate(
            target,
            AttachmentEvidenceStatus.ConfirmedUnreferenced,
            IsRegularFile: true,
            new FileInfo(target).Length,
            new DateTimeOffset(new FileInfo(target).LastWriteTimeUtc, TimeSpan.Zero),
            Sha256Of(target));
        var plan = new AttachmentCleanupSelection().Select(
            fx.DshHome,
            [candidate],
            selectTarget ? [target] : []).Plan!;
        return (fx, plan, target, keep);
    }

    private static (BackupManifest Manifest, VerifiedBackup Backup) BackupFor(FixtureRoot fx, string target)
    {
        var manifest = new BackupManifestBuilder().Build(fx.DshHome, [], [target]).Manifest!;
        var zipDir = Path.Combine(fx.Root, "backups");
        Directory.CreateDirectory(zipDir);
        var zipPath = Path.Combine(zipDir, "attachments.zip");
        var result = new BackupService().CreateBackup(manifest, zipPath);
        Assert.True(result.Success, result.Message);
        return (manifest, result.Backup!);
    }

    [Fact]
    public void Executor_BackupThenConfirm_DeletesSelected_KeepsUnselected()
    {
        var (fx, plan, target, keep) = SetupAttachments();
        using (fx)
        {
            var (manifest, backup) = BackupFor(fx, target);
            var keepBefore = Sha256Of(keep);
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(plan, manifest, backup, fx.DshHome, userConfirmed: true);

            Assert.False(result.Aborted);
            Assert.True(result.AllSucceeded);
            Assert.False(File.Exists(target));
            Assert.Equal(keepBefore, Sha256Of(keep));
        }
    }

    [Fact]
    public void Executor_WithoutBackup_ConfirmationStillRequired_DeletesWhenConfirmed()
    {
        var (fx, plan, target, _) = SetupAttachments();
        using (fx)
        {
            var probe = new HarnessProcessProbe(() => []);

            // 未确认：确认门禁仍生效（N-04 保留项 D4）。
            var notConfirmed = new CleanupExecutor(probe).Execute(plan, null, null, fx.DshHome, userConfirmed: false);
            Assert.True(notConfirmed.Aborted);
            Assert.Equal(CleanupExecutor.GateNotConfirmedCode, notConfirmed.AbortCode);
            Assert.True(File.Exists(target));

            // 已确认、无备份：删除成功且逐项报告（N-04/D1 备份可选）。
            var result = new CleanupExecutor(probe).Execute(plan, null, null, fx.DshHome, userConfirmed: true);
            Assert.False(result.Aborted);
            Assert.True(result.AllSucceeded);
            Assert.Single(result.Outcomes);
            Assert.False(File.Exists(target));
        }
    }

    [Fact]
    public void Executor_WithoutUserConfirmation_NotDeleted()
    {
        var (fx, plan, target, _) = SetupAttachments();
        using (fx)
        {
            var (manifest, backup) = BackupFor(fx, target);
            var probe = new HarnessProcessProbe(() => []);

            var result = new CleanupExecutor(probe).Execute(plan, manifest, backup, fx.DshHome, userConfirmed: false);

            Assert.True(result.Aborted);
            Assert.Equal(CleanupExecutor.GateNotConfirmedCode, result.AbortCode);
            Assert.True(File.Exists(target));
        }
    }

    [Fact]
    public void Executor_ActiveProcess_Blocked_EvenWithBackup()
    {
        var (fx, plan, target, _) = SetupAttachments();
        using (fx)
        {
            var (manifest, backup) = BackupFor(fx, target);
            var active = new HarnessProcessProbe(() =>
                [new ProcessSnapshot(99, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]);

            var result = new CleanupExecutor(active).Execute(plan, manifest, backup, fx.DshHome, userConfirmed: true);

            Assert.True(result.Aborted);
            Assert.StartsWith(CleanupExecutor.GateProcessCode, result.AbortCode!, StringComparison.Ordinal);
            Assert.True(File.Exists(target));
        }
    }
}
