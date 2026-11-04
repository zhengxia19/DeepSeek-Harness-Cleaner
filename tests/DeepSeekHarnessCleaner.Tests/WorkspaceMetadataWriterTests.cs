using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WorkspaceMetadataWriterTests
{
    private static HarnessProcessProbe Inactive() => new(() => []);
    private static (WorkspaceMetadataPlan Plan, WorkspaceBackupEvidence Backup) Scenario(FixtureRoot root,
        Action<JsonObject>? customize = null, bool skipPinned = false, bool bom = false)
    {
        var fixture = new WorkspaceFixture(root);
        var document = fixture.CreateDocument();
        customize?.Invoke(document);
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(document.ToJsonString()));
        if (bom) File.WriteAllBytes(fixture.WorkspacePath, new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(File.ReadAllBytes(fixture.WorkspacePath)).Concat(new byte[] { 13, 10, 32 }).ToArray());
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var pinned = fixture.WriteSession(WorkspaceFixture.PinnedId);
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var cleanup = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, sessions.Select(x => x.SessionId!).ToArray(), cleanup);
        if (skipPinned) File.AppendAllText(pinned, "\n");
        var execution = new CleanupExecutor(Inactive()).Execute(cleanup, null, null, root.SessionsRoot, true);
        Assert.False(execution.Aborted);
        var facts = execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath,
            x.Status == ItemStatus.Deleted ? WorkspacePhysicalStatus.Deleted : WorkspacePhysicalStatus.Skipped)).ToArray();
        var plan = new WorkspaceMetadataProposal().Build(cleanup, before, facts, new PostCleanupAuditor().Audit(root.SessionsRoot, cleanup, before));
        Assert.Equal(WorkspaceSyncState.Proposed, plan.State);
        var backupRoot = Directory.CreateDirectory(Path.Combine(root.Root, "metadata-backups")).FullName;
        var backup = new WorkspaceBackupService(Inactive()).Create(plan, backupRoot);
        Assert.True(backup.Success, backup.ErrorCode);
        return (plan, backup.Backup!);
    }

    [Fact]
    public void ValidatedStorageBindingAllowsSyntheticWrite_AndDriftBlocksPreparedWrite()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var config = new StorageGateFixture(root.DshHome);
        config.Initialize();
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), fixtureBinding: config.Read);
        var prepared = writer.Prepare(plan, backup);
        Assert.True(prepared.Success, prepared.ErrorCode);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        config.Local[config.UserPatch] = FixtureFactory.Utf8("- id: unrelated\n  config: {model: changed}");
        var changed = writer.Apply(prepared.Preparation!);
        Assert.Equal(WorkspaceSyncState.Blocked, changed.State);
        Assert.Equal("SYNC_STORAGE_BINDING_CHANGED", changed.ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        var fresh = writer.Prepare(plan, backup);
        Assert.True(fresh.Success, fresh.ErrorCode);
        Assert.Equal(WorkspaceSyncState.Applied, writer.Apply(fresh.Preparation!).State);
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
    }

    [Fact]
    public void StorageBindingIsRecheckedAtLastReplaceBoundary()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var config = new StorageGateFixture(root.DshHome);
        config.Initialize();
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), point =>
        {
            if (point == WorkspaceWritePoint.BeforeReplace)
                config.Local[config.UserPatch] = FixtureFactory.Utf8("- id: storage-domain\n  config: {backend: other}");
        }, config.Read);
        var prepared = writer.Prepare(plan, backup);
        Assert.True(prepared.Success, prepared.ErrorCode);
        var result = writer.Apply(prepared.Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal("SYNC_PRODUCTION_VERSION_NOT_ALLOWED", result.ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
    }

    [Fact]
    public void DefaultProductionGateCannotBeOverriddenByCallerDto()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var before = File.ReadAllBytes(plan.WorkspacePath);
        var result = new WorkspaceMetadataWriter(Inactive()).Prepare(plan, backup);
        Assert.False(result.Success);
        Assert.Equal("SYNC_PRODUCTION_VERSION_NOT_ALLOWED", result.ErrorCode);
        Assert.Equal("dsh-desktop@0.2.1-rc.2/workspace/v2/json-single", Assert.Single(WorkspaceMetadataPlan.ProductionWriteContractIds));
        Assert.Equal(before, File.ReadAllBytes(plan.WorkspacePath));
    }

    [Fact]
    public void ApplyFiltersOnlyEligibleIds_PreservesOtherStateAndRawBackup_AndIsSingleUse()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var (plan, backup) = Scenario(root, skipPinned: true);
        var before = File.ReadAllBytes(plan.WorkspacePath);
        var old = JsonNode.Parse(before)!;
        var unselectedLog = File.ReadAllBytes(plan.PhysicalTargets.Single(x => x.OwnerSessionId == WorkspaceFixture.PinnedId).AbsolutePath);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var prepared = writer.Prepare(plan, backup);
        Assert.True(prepared.Success, prepared.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(plan.WorkspacePath));
        var started = DateTimeOffset.UtcNow;
        var result = writer.Apply(prepared.Preparation!);
        Assert.True(result.State == WorkspaceSyncState.Applied, result.ErrorCode);
        Assert.Null(result.ErrorCode);
        Assert.InRange(result.AppliedUtc!.Value, started, DateTimeOffset.UtcNow);
        var intent = JsonNode.Parse(File.ReadAllBytes(result.JournalPath!))!;
        Assert.Equal(1, intent["schemaVersion"]!.GetValue<int>());
        Assert.Equal(plan.OperationId, intent["operationId"]!.GetValue<Guid>());
        Assert.Equal(plan.SourceSha256, intent["source"]!["sha256"]!.GetValue<string>());
        Assert.Equal(result.AppliedSha256, intent["candidateSha256"]!.GetValue<string>());
        var current = JsonNode.Parse(File.ReadAllBytes(plan.WorkspacePath))!;
        Assert.Empty(current["global"]!["archivedSessionIds"]!.AsArray());
        Assert.Equal(old["global"]!["pinnedSessionIds"]!.ToJsonString(), current["global"]!["pinnedSessionIds"]!.ToJsonString());
        Assert.Equal(old["global"]!["workspaceIds"]!.ToJsonString(), current["global"]!["workspaceIds"]!.ToJsonString());
        Assert.Equal(old["tables"]!["workspaces"]!["synthetic-b"]!.ToJsonString(), current["tables"]!["workspaces"]!["synthetic-b"]!.ToJsonString());
        var a = current["tables"]!["workspaces"]!["synthetic-a"]!;
        Assert.Equal(WorkspaceFixture.PinnedId, Assert.Single(a["sessionIds"]!.AsArray())!.GetValue<string>());
        Assert.NotEqual(old["tables"]!["workspaces"]!["synthetic-a"]!["updatedAt"]!.GetValue<string>(), a["updatedAt"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(backup.RawPath));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "workspace.replaced.json")));
        Assert.True(new WorkspaceBackupService(Inactive()).Verify(plan, backup).Success);
        Assert.Equal(unselectedLog, File.ReadAllBytes(plan.PhysicalTargets.Single(x => x.OwnerSessionId == WorkspaceFixture.PinnedId).AbsolutePath));
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.False(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
        Assert.Empty(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
        Assert.Equal(WorkspaceSyncState.Blocked, writer.Apply(prepared.Preparation!).State);
    }

    [Fact]
    public void OptionalMissingArraysAndUnchangedWorkspaceFieldsRemainMissingOrUnchanged()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root, doc => doc["global"]!.AsObject().Remove("pinnedSessionIds"));
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.True(result.State == WorkspaceSyncState.Applied, result.ErrorCode);
        var current = JsonNode.Parse(File.ReadAllBytes(plan.WorkspacePath))!;
        Assert.False(current["global"]!.AsObject().ContainsKey("pinnedSessionIds"));
        Assert.Empty(current["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"]!.AsArray());
        Assert.Equal("2026-01-01T00:00:00Z", current["tables"]!["workspaces"]!["synthetic-b"]!["updatedAt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("unit")]
    [InlineData("global")]
    [InlineData("tables")]
    [InlineData("record")]
    public void UnknownFieldSemanticsRefusePreparation(string level)
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root, doc =>
        {
            var node = level switch
            {
                "root" => doc,
                "unit" => doc["unit"]!,
                "global" => doc["global"]!,
                "tables" => doc["tables"]!,
                _ => doc["tables"]!["workspaces"]!["synthetic-a"]!,
            };
            node["unknown"] = "preserve-never-guess";
        });
        var before = File.ReadAllBytes(plan.WorkspacePath);
        Assert.False(new WorkspaceMetadataWriter(root.DshHome, Inactive()).Prepare(plan, backup).Success);
        Assert.Equal(before, File.ReadAllBytes(plan.WorkspacePath));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("uninitialized")]
    [InlineData("pending")]
    [InlineData("duplicate")]
    [InlineData("layout")]
    [InlineData("backup")]
    [InlineData("source")]
    [InlineData("new-log")]
    [InlineData("physical-proof")]
    [InlineData("root-fingerprint")]
    [InlineData("reference")]
    [InlineData("session-proof")]
    [InlineData("active")]
    [InlineData("unknown-process")]
    [InlineData("canceled")]
    public void InvalidCurrentGatesBlockBeforeAnyWrite(string fault)
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        if (fault is "version" or "uninitialized" or "pending")
        {
            var doc = JsonNode.Parse(File.ReadAllBytes(plan.WorkspacePath))!;
            if (fault == "version") doc["unit"]!["version"] = 99;
            if (fault == "uninitialized") doc["global"]!["initialized"] = false;
            if (fault == "pending") doc["global"]!["pendingMutation"] = null;
            File.WriteAllText(plan.WorkspacePath, doc.ToJsonString());
        }
        if (fault == "duplicate") File.WriteAllText(plan.WorkspacePath, "{\"unit\":{},\"unit\":{}}");
        if (fault == "layout") Directory.CreateDirectory(Path.Combine(root.StoragesRoot, "workspace"));
        if (fault == "backup") File.AppendAllText(backup.RawPath, "changed");
        if (fault == "source") File.AppendAllText(plan.WorkspacePath, " ");
        if (fault == "new-log") new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        if (fault == "physical-proof") plan = plan with { DeletionOutcomes = [] };
        if (fault == "root-fingerprint") plan = plan with { RootFingerprint = "wrong" };
        if (fault == "reference") plan = plan with { ReferencesToRemove = plan.ReferencesToRemove.Take(1).ToArray() };
        if (fault == "session-proof") plan = plan with { Sessions = [] };
        var probe = fault switch
        {
            "active" => new HarnessProcessProbe(() => [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]),
            "unknown-process" => new HarnessProcessProbe(() => throw new InvalidOperationException()),
            _ => Inactive(),
        };
        var before = File.ReadAllBytes(plan.WorkspacePath);
        var names = Directory.GetFileSystemEntries(root.StoragesRoot).Order().ToArray();
        var result = new WorkspaceMetadataWriter(root.DshHome, probe).Prepare(plan, backup, fault == "canceled" ? new CancellationToken(true) : default);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Equal(names, Directory.GetFileSystemEntries(root.StoragesRoot).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameBytesDifferentFileIdentityOrContentChangeAfterPrepareBlocksApply(bool identical)
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var prepared = writer.Prepare(plan, backup).Preparation!;
        var original = File.ReadAllBytes(plan.WorkspacePath);
        if (identical)
        {
            File.Move(plan.WorkspacePath, plan.WorkspacePath + ".external-old");
            File.WriteAllBytes(plan.WorkspacePath, original);
        }
        else File.AppendAllText(plan.WorkspacePath, " ");
        var changed = File.ReadAllBytes(plan.WorkspacePath);
        var result = writer.Apply(prepared);
        Assert.Equal(WorkspaceSyncState.Blocked, result.State);
        Assert.Equal(changed, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InterruptedApplyRetainsEvidenceAndBlocksBlindRetry(int point)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel", FixtureFactory.Utf8("protected"));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if ((int)at == point) throw new OperationCanceledException();
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal("SYNC_CANCELED", result.ErrorCode);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.True(File.Exists(result.JournalPath));
        Assert.True(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
        if (point < (int)WorkspaceWritePoint.Replaced) Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        else Assert.DoesNotContain(new WorkspaceReferenceReader().Read(root.DshHome).References, x => x.SessionId == WorkspaceFixture.ArchivedId);
        Assert.False(new WorkspaceMetadataWriter(root.DshHome, Inactive()).Prepare(plan, backup).Success);
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Fact]
    public void ActualDirectoryWriteAclDenialFailsWithoutSourceMutation_AndAclIsRestored()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var preparation = writer.Prepare(plan, backup).Preparation!;
        var originalBytes = File.ReadAllBytes(plan.WorkspacePath);
        var directory = new DirectoryInfo(root.StoragesRoot);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(root.StoragesRoot, "denial-proof"), "x"));
            Assert.Equal(WorkspaceSyncState.Blocked, writer.Apply(preparation).State);
            Assert.Equal(originalBytes, File.ReadAllBytes(plan.WorkspacePath));
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
        Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("source-identity")]
    [InlineData("candidate")]
    [InlineData("process")]
    [InlineData("new-log")]
    public void ChangesDuringCandidateStageAreRecheckedBeforeReplacement(string fault)
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var active = false;
        var probe = new HarnessProcessProbe(() => active
            ? [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)] : []);
        var writer = new WorkspaceMetadataWriter(root.DshHome, probe, at =>
        {
            if (at != WorkspaceWritePoint.CandidateFlushed) return;
            if (fault == "backup") File.AppendAllText(backup.RawPath, "changed");
            if (fault == "source-identity")
            {
                File.Move(plan.WorkspacePath, plan.WorkspacePath + ".external-old");
                File.WriteAllBytes(plan.WorkspacePath, original);
            }
            if (fault == "candidate")
            {
                var candidate = Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
                File.Move(candidate, candidate + ".external-old");
                File.WriteAllText(candidate, "corrupt candidate");
            }
            if (fault == "process") active = true;
            if (fault == "new-log") new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "workspace.replaced.json")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("backup")]
    [InlineData("lock")]
    [InlineData("candidate")]
    public void ActualReparseSubstitutionCannotWriteOutsideFixture(string fault)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.json", original);
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at != WorkspaceWritePoint.CandidateFlushed || fault != "candidate") return;
            var candidate = Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
            File.Delete(candidate);
            File.CreateSymbolicLink(candidate, sentinel);
        });
        var prepared = writer.Prepare(plan, backup).Preparation!;
        if (fault == "source")
        {
            File.Move(plan.WorkspacePath, plan.WorkspacePath + ".preserved");
            File.CreateSymbolicLink(plan.WorkspacePath, sentinel);
        }
        if (fault == "backup")
        {
            File.Delete(backup.RawPath);
            File.CreateSymbolicLink(backup.RawPath, sentinel);
        }
        if (fault == "lock") File.CreateSymbolicLink(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock"), sentinel);
        Assert.NotEqual(WorkspaceSyncState.Applied, writer.Apply(prepared).State);
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
    }

    [Fact]
    public void AncestorLeaseBlocksDirectoryReplacementDuringApply()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var exercised = false;
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at != WorkspaceWritePoint.CandidateFlushed) return;
            Assert.Throws<IOException>(() => Directory.Move(root.StoragesRoot, root.StoragesRoot + "-external"));
            exercised = true;
        });
        Assert.Equal(WorkspaceSyncState.Applied, writer.Apply(writer.Prepare(plan, backup).Preparation!).State);
        Assert.True(exercised);
    }

    [Fact]
    public void SharingFailureBlocksBeforeMutation_AndInterruptedOperationBlocksDifferentBackupRootRetry()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var preparation = writer.Prepare(plan, backup).Preparation!;
        using (var blockingReader = new FileStream(plan.WorkspacePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(WorkspaceSyncState.Blocked, writer.Apply(preparation).State);
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
        var interrupted = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.IntentFlushed) throw new OperationCanceledException();
        });
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, interrupted.Apply(interrupted.Prepare(plan, backup).Preparation!).State);
        var newPlan = plan with { OperationId = Guid.NewGuid() };
        var newBackup = new WorkspaceBackupService(Inactive()).Create(newPlan, Directory.CreateDirectory(Path.Combine(root.Root, "different-backups")).FullName).Backup!;
        Assert.False(new WorkspaceMetadataWriter(root.DshHome, Inactive()).Prepare(newPlan, newBackup).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeasesDenyContentChangesAtCommitAndAfterReplacement(bool afterReplacement)
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var conflict = FixtureFactory.Utf8("external concurrent contents retained");
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at == (afterReplacement ? WorkspaceWritePoint.Replaced : WorkspaceWritePoint.CommitWindow))
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(plan.WorkspacePath, conflict));
                throw new OperationCanceledException();
            }
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
        var previous = Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "workspace.replaced.json");
        if (afterReplacement)
        {
            Assert.Equal(original, File.ReadAllBytes(previous));
            Assert.DoesNotContain(new WorkspaceReferenceReader().Read(root.DshHome).References, x => x.SessionId == WorkspaceFixture.ArchivedId);
        }
        else
        {
            Assert.False(File.Exists(previous));
            Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
        }
        Assert.True(File.Exists(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock")));
    }

    [Fact]
    public void PreparationCannotBeTransferredToAnotherWriter_OrEscapeSyntheticScope()
    {
        using var root = FixtureRoot.Create();
        using var other = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var prepared = writer.Prepare(plan, backup).Preparation!;
        Assert.Equal(WorkspaceSyncState.Blocked, new WorkspaceMetadataWriter(root.DshHome, Inactive()).Apply(prepared).State);
        Assert.False(new WorkspaceMetadataWriter(other.DshHome, Inactive()).Prepare(plan, backup).Success);
        Assert.Equal(WorkspaceSyncState.Applied, writer.Apply(prepared).State);
    }

    [Fact]
    public void ActualHardlinkedSourceIsRejectedByFileIdentityGate()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var link = Path.Combine(root.Root, "source-alias.json");
        Assert.True(CreateHardLinkW(link, plan.WorkspacePath, IntPtr.Zero));
        var original = File.ReadAllBytes(link);
        Assert.False(new WorkspaceMetadataWriter(root.DshHome, Inactive()).Prepare(plan, backup).Success);
        Assert.Equal(original, File.ReadAllBytes(link));
        Assert.Equal(original, File.ReadAllBytes(plan.WorkspacePath));
    }

    [Fact]
    public void NewLogsObservedAfterReplacementCannotBeReportedApplied()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at == WorkspaceWritePoint.Replaced) new WorkspaceFixture(root).WriteSession(WorkspaceFixture.ArchivedId);
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal("SYNC_PHYSICAL_PROOF_INVALID", result.ErrorCode);
        Assert.True(new WorkspaceBackupService(Inactive()).Verify(plan, backup).Success);
    }

    [Fact]
    public void ArchiveAndPinOnlyChangesDoNotRefreshAnyWorkspaceTimestamp()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root, doc => doc["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"] = new JsonArray());
        var originalRecords = JsonNode.Parse(File.ReadAllBytes(plan.WorkspacePath))!["tables"]!.ToJsonString();
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        Assert.Equal(WorkspaceSyncState.Applied, writer.Apply(writer.Prepare(plan, backup).Preparation!).State);
        Assert.Equal(originalRecords, JsonNode.Parse(File.ReadAllBytes(plan.WorkspacePath))!["tables"]!.ToJsonString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existingName, IntPtr security);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitWindowReparseConflictPreservesOutsideSentinel(bool candidateLink)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.json", original);
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive(), at =>
        {
            if (at != WorkspaceWritePoint.CommitWindow) return;
            var path = candidateLink ? Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp")) : plan.WorkspacePath;
            File.Move(path, path + ".preserved");
            File.CreateSymbolicLink(path, sentinel);
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
    }

    [Theory]
    [InlineData("interrupt")]
    [InlineData("new-file")]
    [InlineData("new-link")]
    [InlineData("process")]
    public void BetweenRenamesInterruptionOrConflictRetainsOriginalCandidateAndNewTarget(string fault)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var (plan, backup) = Scenario(root);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var conflict = FixtureFactory.Utf8("external target must survive");
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel", conflict);
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var active = false;
        var probe = new HarnessProcessProbe(() => active
            ? [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)] : []);
        var writer = new WorkspaceMetadataWriter(root.DshHome, probe, at =>
        {
            if (at != WorkspaceWritePoint.OriginalRetained) return;
            Assert.False(File.Exists(plan.WorkspacePath));
            if (fault == "interrupt") throw new OperationCanceledException();
            if (fault == "new-file") File.WriteAllBytes(plan.WorkspacePath, conflict);
            if (fault == "new-link") File.CreateSymbolicLink(plan.WorkspacePath, sentinel);
            if (fault == "process") active = true;
        });
        var result = writer.Apply(writer.Prepare(plan, backup).Preparation!);
        Assert.Equal(WorkspaceSyncState.RecoveryRequired, result.State);
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "workspace.replaced.json")));
        Assert.Single(Directory.GetFiles(root.StoragesRoot, "*.tmp"));
        if (fault is "interrupt" or "process") Assert.False(File.Exists(plan.WorkspacePath));
        else Assert.Equal(conflict, File.ReadAllBytes(plan.WorkspacePath));
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
        Assert.False(new WorkspaceMetadataWriter(root.DshHome, Inactive()).Prepare(plan, backup).Success);
    }

    [Fact]
    public void OriginalBomWhitespaceAndExplicitProtectedAclArePreservedInBackupAndReplacementSecurity()
    {
        using var root = FixtureRoot.Create();
        var (plan, backup) = Scenario(root, bom: true);
        var original = File.ReadAllBytes(plan.WorkspacePath);
        var info = new FileInfo(plan.WorkspacePath);
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var security = info.GetAccessControl(sections);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        info.SetAccessControl(security);
        var sddl = info.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
        var writer = new WorkspaceMetadataWriter(root.DshHome, Inactive());
        var prepared = writer.Prepare(plan, backup);
        Assert.True(prepared.Success, prepared.ErrorCode);
        var result = writer.Apply(prepared.Preparation!);
        Assert.True(result.State == WorkspaceSyncState.Applied, result.ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(backup.RawPath));
        Assert.Equal(sddl, new FileInfo(plan.WorkspacePath).GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections));
    }
}
