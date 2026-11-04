using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WorkspaceSyncTests
{
    private static HarnessProcessProbe Inactive() => new(() => []);
    private static (CleanupPlan Plan, PreCleanupAudit Before, CleanupExecutionResult Execution, string BackupRoot) Scenario(FixtureRoot root, bool skipPinned = false)
    {
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var pinned = fixture.WriteSession(WorkspaceFixture.PinnedId);
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var plan = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), plan);
        if (skipPinned) File.AppendAllText(pinned, "\n");
        var execution = new CleanupExecutor(Inactive()).Execute(plan, null, null, root.SessionsRoot, true);
        Assert.False(execution.Aborted);
        return (plan, before, execution, Directory.CreateDirectory(Path.Combine(root.Root, "metadata-backups")).FullName);
    }

    [Fact]
    public void PartialPhysicalSuccessSyncsOnlySuccessfulSession_AndRetryCannotPromoteFailedSession()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel", FixtureFactory.Utf8("protected"));
        var hash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var source = Scenario(root, skipPinned: true);
        var skipped = source.Plan.Targets.Single(x => x.OwnerSessionId == WorkspaceFixture.PinnedId).AbsolutePath;
        var skippedBytes = File.ReadAllBytes(skipped);
        var coordinator = new WorkspaceSyncCoordinator(root.DshHome, Inactive());
        var result = coordinator.Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        Assert.Equal(WorkspaceSyncState.Applied, result.Outcome.State);
        Assert.Equal(2, result.Outcome.RemovedReferenceCount);
        Assert.Equal(1, source.Execution.DeletedCount);
        Assert.Equal(1, source.Execution.SkippedCount);
        Assert.Equal(2, result.Audit!.Sessions.Single(x => x.SessionId == WorkspaceFixture.PinnedId).WorkspaceReferenceCount);
        Assert.Equal(0, result.Audit.Sessions.Single(x => x.SessionId == WorkspaceFixture.ArchivedId).WorkspaceReferenceCount);
        var applied = File.ReadAllBytes(result.Plan!.WorkspacePath);
        var retry = coordinator.Retry(source.Plan, source.Before, source.Execution, result);
        Assert.Equal(WorkspaceSyncState.Blocked, retry.Outcome.State); // Failed pinned session still excluded; no eligible remaining removals.
        Assert.Equal(0, retry.Outcome.RemovedReferenceCount);
        Assert.Equal(applied, File.ReadAllBytes(result.Plan.WorkspacePath));
        Assert.Equal(skippedBytes, File.ReadAllBytes(skipped));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptOutOrUnverifiedProductionNeverCreatesMetadataBackupOrWrites(bool requested)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var bytes = File.ReadAllBytes(Path.Combine(root.StoragesRoot, "workspace.json"));
        var result = new WorkspaceSyncCoordinator(Inactive()).Run(source.Plan, source.Before, source.Execution, requested, source.BackupRoot);
        Assert.Equal(requested ? WorkspaceSyncState.Blocked : WorkspaceSyncState.NotRequested, result.Outcome.State);
        Assert.Empty(Directory.GetFileSystemEntries(source.BackupRoot));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root.StoragesRoot, "workspace.json")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void InterruptedOperationIsExplicitlyReconciled_ThenFreshRetryDoesNotRestoreLogs(int point)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel", FixtureFactory.Utf8("protected"));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var source = Scenario(root);
        var original = File.ReadAllBytes(Path.Combine(root.StoragesRoot, "workspace.json"));
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if ((int)at == point) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, broken.Outcome.State);
        Assert.Null(broken.Outcome.RemovedReferenceCount);
        var coordinator = new WorkspaceSyncCoordinator(root.DshHome, Inactive());
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, coordinator.Retry(source.Plan, source.Before, source.Execution, broken).Outcome.State);
        var recovery = new WorkspaceRecoveryService(root.DshHome, Inactive());
        var observed = recovery.Inspect(broken.Plan!, broken.Backup!);
        Assert.NotEqual(WorkspaceRecoveryState.Blocked, observed.State);
        var result = recovery.Resolve(broken.Plan!, broken.Backup!);
        Assert.True(result.Resolved, result.ErrorCode);
        Assert.Equal(point is 3 or 4 ? WorkspaceRecoveryState.ResolvedApplied : WorkspaceRecoveryState.ResolvedOriginal, result.State);
        Assert.Equal(result.State, recovery.Resolve(broken.Plan!, broken.Backup!).State);
        Assert.Equal(original, File.ReadAllBytes(broken.Backup!.RawPath));
        Assert.All(source.Plan.Targets, target => Assert.False(File.Exists(target.AbsolutePath)));
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        var retried = coordinator.Retry(source.Plan, source.Before, source.Execution, broken);
        Assert.Equal(point is 3 or 4 ? WorkspaceSyncState.NoOp : WorkspaceSyncState.Applied, retried.Outcome.State);
        if (retried.Outcome.State == WorkspaceSyncState.Applied) Assert.NotEqual(broken.Plan!.OperationId, retried.Plan!.OperationId);
        Assert.All(source.Plan.Targets, target => Assert.False(File.Exists(target.AbsolutePath)));
        Assert.DoesNotContain(new WorkspaceReferenceReader().Read(root.DshHome).References, x => x.SessionId is WorkspaceFixture.ArchivedId or WorkspaceFixture.PinnedId);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("same-bytes-new-identity")]
    [InlineData("new-source-at-gap")]
    [InlineData("backup")]
    [InlineData("intent")]
    [InlineData("duplicate-intent")]
    [InlineData("unknown-intent")]
    [InlineData("missing-intent-field")]
    [InlineData("null-intent-source")]
    [InlineData("marker")]
    [InlineData("candidate")]
    [InlineData("link")]
    [InlineData("active")]
    [InlineData("unknown-process")]
    [InlineData("canceled")]
    public void ChangedOrUnverifiableRecoveryMaterialsArePreservedAndRejected(string fault)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var source = Scenario(root);
        var point = fault == "new-source-at-gap" ? WorkspaceWritePoint.OriginalRetained : WorkspaceWritePoint.CandidateFlushed;
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == point) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, broken.Outcome.State);
        var plan = broken.Plan!;
        var backup = broken.Backup!;
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel", FixtureFactory.Utf8("protected"));
        var hash = SHA256.HashData(File.ReadAllBytes(sentinel));
        if (fault is "source" or "new-source-at-gap") File.WriteAllText(plan.WorkspacePath, "external current data");
        if (fault == "same-bytes-new-identity")
        {
            File.Move(plan.WorkspacePath, plan.WorkspacePath + ".external");
            File.Copy(plan.WorkspacePath + ".external", plan.WorkspacePath);
        }
        if (fault == "backup") File.AppendAllText(backup.RawPath, "changed");
        var intent = Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "write-intent.json");
        if (fault == "intent") File.WriteAllText(intent, "{");
        if (fault == "duplicate-intent") File.WriteAllText(intent, "{\"schemaVersion\":1,\"schemaVersion\":1}");
        if (fault == "unknown-intent")
        {
            var json = JsonNode.Parse(File.ReadAllBytes(intent))!;
            json["unknown"] = true;
            File.WriteAllText(intent, json.ToJsonString());
        }
        if (fault is "missing-intent-field" or "null-intent-source")
        {
            var json = JsonNode.Parse(File.ReadAllBytes(intent))!;
            if (fault == "missing-intent-field") json.AsObject().Remove("source");
            else json["source"] = null;
            File.WriteAllText(intent, json.ToJsonString());
        }
        if (fault == "marker") File.WriteAllText(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock"), new string('0', 32));
        var candidate = Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
        if (fault == "candidate") File.AppendAllText(candidate, "changed");
        if (fault == "link") { File.Delete(candidate); File.CreateSymbolicLink(candidate, sentinel); }
        var probe = fault switch
        {
            "active" => new HarnessProcessProbe(() => [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]),
            "unknown-process" => new HarnessProcessProbe(() => throw new InvalidOperationException()),
            _ => Inactive(),
        };
        var before = Directory.GetFiles(root.Root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        var service = new WorkspaceRecoveryService(root.DshHome, probe);
        var result = service.Resolve(plan, backup, fault == "canceled" ? new CancellationToken(true) : default);
        Assert.False(result.Resolved);
        Assert.Equal(WorkspaceRecoveryState.Blocked, result.State);
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecoveryCanBeRecheckedAfterItsOwnInterruption_WithoutDeletingEvidence(int point)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.OriginalRetained) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        var interrupted = new WorkspaceRecoveryService(root.DshHome, Inactive(), at =>
        {
            if ((int)at == point) throw new OperationCanceledException();
        }).Resolve(broken.Plan!, broken.Backup!);
        Assert.False(interrupted.Resolved);
        var result = new WorkspaceRecoveryService(root.DshHome, Inactive()).Resolve(broken.Plan!, broken.Backup!);
        Assert.Equal(WorkspaceRecoveryState.ResolvedOriginal, result.State);
        Assert.Equal(File.ReadAllBytes(broken.Backup!.RawPath), File.ReadAllBytes(broken.Plan!.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(broken.Backup.RawPath)!, "recovery-candidate.json")));
        Assert.All(source.Plan.Targets, x => Assert.False(File.Exists(x.AbsolutePath)));
    }

    [Fact]
    public void RetryUsesFreshSnapshotAndAbsence_NotThePriorProposal()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.CandidateFlushed) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        Assert.True(new WorkspaceRecoveryService(root.DshHome, Inactive()).Resolve(broken.Plan!, broken.Backup!).Resolved);
        new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        var coordinator = new WorkspaceSyncCoordinator(root.DshHome, Inactive());
        var retry = coordinator.Retry(source.Plan, source.Before, source.Execution, broken);
        Assert.Equal(WorkspaceSyncState.Applied, retry.Outcome.State);
        Assert.All(retry.Plan!.ReferencesToRemove, x => Assert.Equal(WorkspaceFixture.PinnedId, x.SessionId));
        Assert.Equal(2, new WorkspaceReferenceReader().Read(root.DshHome).References.Count(x => x.SessionId == WorkspaceFixture.ArchivedId));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("candidate-rename")]
    [InlineData("directory-rename")]
    [InlineData("process")]
    public void LateRecoveryConflictsNeverOverwriteOrClearTheMarker(string fault)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.OriginalRetained) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        var active = false;
        var exercised = false;
        var probe = new HarnessProcessProbe(() => active
            ? [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)] : []);
        var service = new WorkspaceRecoveryService(root.DshHome, probe, at =>
        {
            if (at != WorkspaceRecoveryPoint.Rechecked) return;
            exercised = true;
            if (fault == "target") File.WriteAllText(broken.Plan!.WorkspacePath, "external new source");
            if (fault == "candidate-rename") File.Move(Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp")), Path.Combine(root.Root, "stolen-candidate"));
            if (fault == "directory-rename") Directory.Move(root.StoragesRoot, root.StoragesRoot + "-external");
            if (fault == "process") active = true;
        });
        Assert.False(service.Resolve(broken.Plan!, broken.Backup!).Resolved);
        Assert.True(exercised);
        Assert.True(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(broken.Backup!.RawPath)!, "workspace.replaced.json")));
        if (fault == "target") Assert.Equal("external new source", File.ReadAllText(broken.Plan!.WorkspacePath));
        else Assert.False(File.Exists(broken.Plan!.WorkspacePath));
    }

    [Fact]
    public void RecoveryHoldsOriginalBackupFilesAgainstLateMutation()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.OriginalRetained) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        var original = File.ReadAllBytes(broken.Backup!.RawPath);
        var recovery = new WorkspaceRecoveryService(root.DshHome, Inactive(), at =>
        {
            if (at != WorkspaceRecoveryPoint.Rechecked) return;
            Assert.Throws<IOException>(() => File.AppendAllText(broken.Backup.RawPath, "concurrent mutation"));
            Assert.Throws<IOException>(() => File.Move(broken.Backup.ManifestPath, broken.Backup.ManifestPath + ".external"));
        });
        Assert.True(recovery.Resolve(broken.Plan!, broken.Backup).Resolved);
        Assert.Equal(original, File.ReadAllBytes(broken.Backup.RawPath));
    }

    [Fact]
    public void CorruptedCompletionRecordCannotClearRecoveryMarker()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.CandidateFlushed) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        var recovery = new WorkspaceRecoveryService(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceRecoveryPoint.RecordFlushed)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(broken.Backup!.RawPath)!, "recovery-resolved.json"), "{}");
        });
        Assert.False(recovery.Resolve(broken.Plan!, broken.Backup!).Resolved);
        Assert.True(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
        Assert.Equal(File.ReadAllBytes(broken.Backup!.RawPath), File.ReadAllBytes(broken.Plan!.WorkspacePath));
    }

    [Fact]
    public void RecoveryPermissionFailureRetainsArtifacts_AndAllowsRecheckAfterAclRestoration()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var broken = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.OriginalRetained) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        var directory = new DirectoryInfo(Path.GetDirectoryName(broken.Backup!.RawPath)!);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(directory.FullName, "denial-proof"), "x"));
            Assert.False(new WorkspaceRecoveryService(root.DshHome, Inactive()).Resolve(broken.Plan!, broken.Backup).Resolved);
            Assert.True(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
            Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
        Assert.Equal(WorkspaceRecoveryState.ResolvedOriginal, new WorkspaceRecoveryService(root.DshHome, Inactive()).Resolve(broken.Plan!, broken.Backup).State);
        Assert.Equal(File.ReadAllBytes(broken.Backup.RawPath), File.ReadAllBytes(broken.Plan!.WorkspacePath));
        Assert.All(source.Plan.Targets, x => Assert.False(File.Exists(x.AbsolutePath)));
    }

    [Fact]
    public void InvalidRetryContextAndAbortedPhysicalExecutionCannotStartMetadataOperation()
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var coordinator = new WorkspaceSyncCoordinator(root.DshHome, Inactive());
        var aborted = coordinator.Run(source.Plan, source.Before, new(true, "synthetic-abort", []), true, source.BackupRoot);
        Assert.Equal(WorkspaceSyncState.Blocked, aborted.Outcome.State);
        var wrong = aborted with { Outcome = aborted.Outcome with { CleanupPlanId = Guid.NewGuid() } };
        Assert.Equal("SYNC_RETRY_CONTEXT_INVALID", coordinator.Retry(source.Plan, source.Before, source.Execution, wrong).Outcome.ErrorCode);
        Assert.Empty(Directory.GetFileSystemEntries(source.BackupRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedOperationWithoutMarkerNeedsExactCompletionRecord(bool corruptRecord)
    {
        using var root = FixtureRoot.Create();
        var source = Scenario(root);
        var interrupted = new WorkspaceSyncCoordinator(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.OutcomeRecorded) throw new OperationCanceledException();
        }).Run(source.Plan, source.Before, source.Execution, true, source.BackupRoot);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, interrupted.Outcome.State);
        Assert.False(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
        var appliedPath = Path.Combine(Path.GetDirectoryName(interrupted.Backup!.RawPath)!, "write-applied.json");
        if (corruptRecord) File.WriteAllText(appliedPath, "{}");
        var before = File.ReadAllBytes(interrupted.Plan!.WorkspacePath);
        var recovery = new WorkspaceRecoveryService(root.DshHome, Inactive());
        Assert.Equal(corruptRecord ? WorkspaceRecoveryState.Blocked : WorkspaceRecoveryState.ResolvedApplied,
            recovery.Inspect(interrupted.Plan, interrupted.Backup).State);
        Assert.Equal(!corruptRecord, recovery.Resolve(interrupted.Plan, interrupted.Backup).Resolved);
        var retried = new WorkspaceSyncCoordinator(root.DshHome, Inactive()).Retry(source.Plan, source.Before, source.Execution, interrupted);
        Assert.Equal(corruptRecord ? WorkspaceSyncState.RecoveryRequired : WorkspaceSyncState.NoOp, retried.Outcome.State);
        Assert.Equal(before, File.ReadAllBytes(interrupted.Plan.WorkspacePath));
        Assert.All(source.Plan.Targets, target => Assert.False(File.Exists(target.AbsolutePath)));
    }
}
