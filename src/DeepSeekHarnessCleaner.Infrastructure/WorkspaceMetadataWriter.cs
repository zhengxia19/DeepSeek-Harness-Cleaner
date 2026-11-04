using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// Guarded single-file Workspace replacement, requiring reviewed release and effective storage bindings.
/// A test-only internal constructor confines actual writes to the owned synthetic fixture hierarchy.
/// </summary>
public sealed class WorkspaceMetadataWriter
{
    private readonly object _owner = new();
    private readonly HarnessProcessProbe _probe;
    private readonly string? _fixtureHome;
    private readonly Action<WorkspaceWritePoint>? _fault;
    private readonly Func<string?>? _fixtureBinding;
    private const string LockName = ".dshcleaner-workspace-sync.lock";
    private static readonly JsonSerializerOptions RecordJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public WorkspaceMetadataWriter(HarnessProcessProbe? probe = null) => _probe = probe ?? new HarnessProcessProbe();

    internal WorkspaceMetadataWriter(string fixtureHome, HarnessProcessProbe probe, Action<WorkspaceWritePoint>? fault = null,
        Func<string?>? fixtureBinding = null)
    {
        _fixtureHome = Path.GetFullPath(fixtureHome);
        _probe = probe;
        _fault = fault;
        _fixtureBinding = fixtureBinding;
    }

    public WorkspacePrepareResult Prepare(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var frozen = plan with
            {
                ReferencesToRemove = plan.ReferencesToRemove.ToArray(),
                Sessions = plan.Sessions.ToArray(),
                BlockReasons = plan.BlockReasons.ToArray(),
                PhysicalTargets = plan.PhysicalTargets.ToArray(),
                DeletionOutcomes = plan.DeletionOutcomes.ToArray(),
                AbsenceEvidence = plan.AbsenceEvidence.ToArray(),
            };
            var storageBinding = ScopeFingerprint(frozen.DataHome);
            Require(storageBinding is not null, "SYNC_PRODUCTION_VERSION_NOT_ALLOWED");
            Check(frozen, backup, ct, storageBinding: storageBinding);
            using var input = Open(frozen.WorkspacePath);
            var identity = WorkspaceFileIdentity.Read(input);
            Require(identity.Sha256 == frozen.SourceSha256, "SYNC_SOURCE_CHANGED");
            _ = WorkspaceMetadataTransform.Load(frozen.WorkspacePath);
            return new(new WorkspaceWritePreparation(_owner, frozen, backup, identity, storageBinding!), null);
        }
        catch (OperationCanceledException) { return new(null, "SYNC_CANCELED"); }
        catch (SyncBlockedException ex) { return new(null, ex.Code); }
        catch (Exception ex) when (Expected(ex)) { return new(null, "SYNC_PREPARE_FAILED"); }
    }

    public WorkspaceWriteResult Apply(WorkspaceWritePreparation preparation, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return new(WorkspaceSyncState.Blocked, "SYNC_PLATFORM_UNSUPPORTED", null, null, null);
        if (!ReferenceEquals(preparation.Owner, _owner) || Interlocked.Exchange(ref preparation.Consumed, 1) != 0)
            return new(WorkspaceSyncState.Blocked, "SYNC_PREPARATION_INVALID_OR_CONSUMED", null, null, null);
        var plan = preparation.Plan;
        var backup = preparation.Backup;
        var directory = Path.GetDirectoryName(plan.WorkspacePath)!;
        var lockPath = Path.Combine(directory, LockName);
        var candidatePath = Path.Combine(directory, ".dshcleaner-workspace-" + plan.OperationId.ToString("N") + ".tmp");
        var journal = Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "write-intent.json");
        var ownsLock = false;
        FileStream? coordination = null;
        try
        {
            using var sourcePins = WorkspaceFileIdentity.PinDirectories(directory);
            using var backupPins = WorkspaceFileIdentity.PinDirectories(Path.GetDirectoryName(backup.RawPath)!);
            Check(plan, backup, ct, storageBinding: preparation.StorageBinding);
            using var input = WorkspaceFileIdentity.OpenForRename(plan.WorkspacePath);
            Require(WorkspaceFileIdentity.Read(input) == preparation.Identity, "SYNC_SOURCE_IDENTITY_CHANGED");
            var original = WorkspaceMetadataTransform.Load(plan.WorkspacePath);
            RequireMissing(plan.DataHome, lockPath);
            coordination = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            ownsLock = true;
            var lockBytes = Encoding.UTF8.GetBytes(plan.OperationId.ToString("N"));
            coordination.Write(lockBytes);
            coordination.Flush(true);
            var appliedUtc = DateTimeOffset.UtcNow;
            var ids = Eligible(plan);
            var bytes = WorkspaceMetadataTransform.Build(original, ids, appliedUtc);
            var candidateHash = Hash(bytes);
            var intent = new WriteIntent(1, plan.OperationId, plan.CleanupPlanId, plan.RootFingerprint,
                preparation.Identity, candidateHash, appliedUtc, backup.ManifestSha256, backup.VerificationSha256);
            WriteNew(Path.GetDirectoryName(backup.RawPath)!, journal, JsonSerializer.SerializeToUtf8Bytes(intent, RecordJson), ct);
            _fault?.Invoke(WorkspaceWritePoint.IntentFlushed);
            WriteNew(plan.DataHome, candidatePath, bytes, ct);
            using var candidate = WorkspaceFileIdentity.OpenForRename(candidatePath);
            var candidateIdentity = WorkspaceFileIdentity.Read(candidate);
            Require(candidateIdentity.Sha256 == candidateHash, "SYNC_CANDIDATE_CHANGED");
            WorkspaceMetadataTransform.Validate(original, WorkspaceMetadataTransform.Load(candidatePath), ids,
                appliedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            _fault?.Invoke(WorkspaceWritePoint.CandidateFlushed);
            Check(plan, backup, ct, ownsLock: true, storageBinding: preparation.StorageBinding);
            Require(WorkspaceFileIdentity.Read(input) == preparation.Identity, "SYNC_SOURCE_IDENTITY_CHANGED");
            using (var current = Open(plan.WorkspacePath))
                Require(WorkspaceFileIdentity.Read(current) == preparation.Identity, "SYNC_SOURCE_IDENTITY_CHANGED");
            using (var current = Open(candidatePath))
                Require(WorkspaceFileIdentity.Read(current) == candidateIdentity, "SYNC_CANDIDATE_CHANGED");
            var operationRoot = Path.GetDirectoryName(backup.RawPath)!;
            var previousPath = Path.Combine(operationRoot, "workspace.replaced.json");
            RequireMissing(operationRoot, previousPath);
            var applying = Path.Combine(operationRoot, "write-applying.json");
            WriteNew(operationRoot, applying, JsonSerializer.SerializeToUtf8Bytes(new { plan.OperationId, candidateHash }, RecordJson), ct);
            _fault?.Invoke(WorkspaceWritePoint.BeforeReplace);
            // Recheck after the deterministic fault seam too. Existing source handles deny new content writers.
            Check(plan, backup, ct, ownsLock: true, storageBinding: preparation.StorageBinding);
            using (var current = Open(plan.WorkspacePath))
                Require(WorkspaceFileIdentity.Read(current) == preparation.Identity, "SYNC_SOURCE_IDENTITY_CHANGED");
            Require(new PathGuard().Check(plan.DataHome, candidatePath).IsAllowed, "SYNC_CANDIDATE_PATH_INVALID");
            using (var current = Open(candidatePath))
                Require(WorkspaceFileIdentity.Read(current) == candidateIdentity, "SYNC_CANDIDATE_CHANGED");
            // Both handles deny external writes/rename; use those identities, never reopen a replacement by name.
            var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            var security = new FileInfo(plan.WorkspacePath).GetAccessControl(sections);
            var copy = new FileSecurity();
            copy.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), sections);
            new FileInfo(candidatePath).SetAccessControl(copy);
            Require(security.GetSecurityDescriptorSddlForm(sections)
                == new FileInfo(candidatePath).GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections), "SYNC_SECURITY_CHANGED");
            _fault?.Invoke(WorkspaceWritePoint.CommitWindow);
            ct.ThrowIfCancellationRequested();
            Require(_probe.Probe().AllowsWrite, "SYNC_PROCESS_NOT_INACTIVE");
            WorkspaceFileIdentity.RenameNoReplace(input, previousPath);
            _fault?.Invoke(WorkspaceWritePoint.OriginalRetained);
            Require(_probe.Probe().AllowsWrite, "SYNC_PROCESS_NOT_INACTIVE");
            RequireMissing(plan.DataHome, plan.WorkspacePath);
            WorkspaceFileIdentity.RenameNoReplace(candidate, plan.WorkspacePath);
            _fault?.Invoke(WorkspaceWritePoint.Replaced);
            // Once replacement is attempted, verify the outcome even if cancellation has arrived.
            Require(new PathGuard().Check(plan.DataHome, plan.WorkspacePath).IsAllowed, "SYNC_POST_PATH_INVALID");
            Require(new PathGuard().Check(operationRoot, previousPath).IsAllowed, "SYNC_PREVIOUS_PATH_INVALID");
            using (var previous = Open(previousPath))
                Require(WorkspaceFileIdentity.Read(previous) == preparation.Identity, "SYNC_REPLACED_SOURCE_MISMATCH");
            using (var current = Open(plan.WorkspacePath))
                Require(WorkspaceFileIdentity.Read(current).Sha256 == candidateHash, "SYNC_POST_HASH_INVALID");
            WorkspaceMetadataTransform.Validate(original, WorkspaceMetadataTransform.Load(plan.WorkspacePath), ids,
                appliedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            var index = new WorkspaceReferenceReader().Read(plan.DataHome, FileShare.Read | FileShare.Delete);
            Require(index.Status == WorkspaceReferenceStatus.Known && index.SnapshotSha256 == candidateHash
                && !index.References.Any(x => ids.Contains(x.SessionId)), "SYNC_POST_REFERENCES_INVALID");
            Require(new WorkspaceBackupService(_probe).Verify(plan, backup).Success, "SYNC_BACKUP_INVALID");
            CheckPhysical(plan, ids, CancellationToken.None);
            var completed = Path.Combine(operationRoot, "write-applied.json");
            WriteNew(operationRoot, completed, JsonSerializer.SerializeToUtf8Bytes(new { plan.OperationId, candidateHash, appliedUtc }, RecordJson), CancellationToken.None);
            _fault?.Invoke(WorkspaceWritePoint.Verified);
            Require(_probe.Probe().AllowsWrite, "SYNC_PROCESS_NOT_INACTIVE");
            coordination.Dispose();
            coordination = null;
            Require(new PathGuard().Check(plan.DataHome, lockPath).IsAllowed && File.ReadAllBytes(lockPath).SequenceEqual(lockBytes), "SYNC_LOCK_CHANGED");
            File.Delete(lockPath); // Only this operation's exact tool lock, after successful replacement verification.
            _fault?.Invoke(WorkspaceWritePoint.OutcomeRecorded);
            return new(WorkspaceSyncState.Applied, null, candidateHash, appliedUtc, journal);
        }
        catch (OperationCanceledException) { return Failure("SYNC_CANCELED"); }
        catch (SyncBlockedException ex) { return Failure(ex.Code); }
        catch (Exception ex) when (Expected(ex)) { return Failure("SYNC_APPLY_FAILED"); }
        finally { coordination?.Dispose(); } // Interrupted artifacts are retained; never auto-rollback or erase evidence.

        WorkspaceWriteResult Failure(string code) => new(ownsLock ? WorkspaceSyncState.RecoveryRequired : WorkspaceSyncState.Blocked,
            code, null, null, ownsLock ? journal : null);
    }

    private void Check(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct, bool ownsLock = false,
        string? storageBinding = null)
    {
        ct.ThrowIfCancellationRequested();
        Require(OperatingSystem.IsWindows(), "SYNC_PLATFORM_UNSUPPORTED");
        // There is deliberately no caller-controlled production allowlist or public "force" switch.
        var currentBinding = ScopeFingerprint(plan.DataHome);
        Require(currentBinding is not null, "SYNC_PRODUCTION_VERSION_NOT_ALLOWED");
        Require(storageBinding is null || storageBinding == currentBinding, "SYNC_STORAGE_BINDING_CHANGED");
        Require(plan.State == WorkspaceSyncState.Proposed && plan.OperationId != Guid.Empty && plan.CleanupPlanId != Guid.Empty
            && plan.BlockReasons.Count == 0 && plan.ReferencesToRemove.Count > 0, "SYNC_PLAN_INVALID");
        Require(plan.WorkspacePath == Path.Combine(Path.GetFullPath(plan.DataHome), "storages", "workspace.json"), "SYNC_SOURCE_PATH_INVALID");
        Require(new CleanupPlanner().Build(Path.Combine(plan.DataHome, "sessions"), [], []).Plan?.RootFingerprint == plan.RootFingerprint,
            "SYNC_ROOT_CHANGED");
        Require(_probe.Probe().AllowsWrite, "SYNC_PROCESS_NOT_INACTIVE");
        var index = new WorkspaceReferenceReader().Read(plan.DataHome, FileShare.Read | FileShare.Delete);
        Require(index.Status == WorkspaceReferenceStatus.Known && index.SnapshotSha256 == plan.SourceSha256, "SYNC_SOURCE_UNKNOWN_OR_CHANGED");
        _ = WorkspaceMetadataTransform.Load(plan.WorkspacePath);
        var ids = Eligible(plan);
        Require(index.References.Where(x => ids.Contains(x.SessionId)).SequenceEqual(plan.ReferencesToRemove), "SYNC_REFERENCES_CHANGED");
        CheckPhysical(plan, ids, ct);
        Require(new WorkspaceBackupService(_probe).Verify(plan, backup).Success, "SYNC_BACKUP_INVALID");
        Require(string.Equals(Path.GetPathRoot(backup.RawPath), Path.GetPathRoot(plan.WorkspacePath), StringComparison.OrdinalIgnoreCase), "SYNC_BACKUP_VOLUME_MISMATCH");
        var directory = Path.GetDirectoryName(plan.WorkspacePath)!;
        if (!ownsLock) RequireMissing(plan.DataHome, Path.Combine(directory, LockName));
        var candidates = Directory.GetFileSystemEntries(directory, ".dshcleaner-workspace-*.tmp");
        Require(candidates.Length == 0 || ownsLock && candidates.Length == 1
            && Path.GetFileName(candidates[0]) == ".dshcleaner-workspace-" + plan.OperationId.ToString("N") + ".tmp", "SYNC_UNFINISHED_CANDIDATE");
        if (!ownsLock) RequireMissing(Path.GetDirectoryName(backup.RawPath)!, Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "write-intent.json"));
    }

    internal static void CheckPhysical(WorkspaceMetadataPlan plan, HashSet<string> ids, CancellationToken ct)
    {
        var guard = new PathGuard();
        foreach (var id in ids)
        {
            var targets = plan.PhysicalTargets.Where(x => x.OwnerSessionId == id).ToArray();
            Require(targets.Any(x => x.Category == CleanupCategory.SessionLog), "SYNC_PHYSICAL_PROOF_INVALID");
        }
        foreach (var target in plan.PhysicalTargets.Where(x => x.OwnerSessionId is null || ids.Contains(x.OwnerSessionId)))
        {
            Require(plan.DeletionOutcomes.Count(x => x.AbsolutePath == target.AbsolutePath) == 1
                && plan.DeletionOutcomes.Single(x => x.AbsolutePath == target.AbsolutePath).Status == WorkspacePhysicalStatus.Deleted
                && plan.AbsenceEvidence.Count(x => x.AbsolutePath == target.AbsolutePath && x.Category == target.Category && x.Presence == TargetPresence.Absent) == 1
                && guard.Check(plan.DataHome, target.AbsolutePath).Status == PathCheckStatus.Missing, "SYNC_PHYSICAL_PROOF_INVALID");
        }
        var scan = new SessionResidueInventory().Scan(plan.DataHome, ids.ToArray(), [], ct);
        var sessionsRoot = Path.Combine(plan.DataHome, "sessions");
        Require(!scan.Issues.Any(x => x.Code.StartsWith("SESSION_IDENTITY_", StringComparison.Ordinal) || x.Code.StartsWith("LOG_", StringComparison.Ordinal)
                || x.EvidencePath is not null && Inside(x.EvidencePath, sessionsRoot))
            && !scan.Sessions.Any(x => x.Items.Any(i => i.Kind == SessionResidueKind.SessionLog)), "SYNC_LOG_ABSENCE_UNKNOWN");
    }

    private bool SyntheticHome(string home)
    {
        if (!string.Equals(Path.GetFullPath(home), _fixtureHome, StringComparison.OrdinalIgnoreCase)) return false;
        var parent = Directory.GetParent(_fixtureHome!)!;
        var prefix = Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures");
        return Path.GetFileName(_fixtureHome) == ".dsh" && parent.Parent?.FullName.Equals(Path.TrimEndingDirectorySeparator(prefix), StringComparison.OrdinalIgnoreCase) == true
            && parent.Name.StartsWith("fx-", StringComparison.Ordinal) && Guid.TryParseExact(parent.Name[3..], "N", out _);
    }

    internal bool AllowsScope(string dataHome) => ScopeFingerprint(dataHome) is not null;

    private string? ScopeFingerprint(string dataHome) => !OperatingSystem.IsWindows() ? null
        : _fixtureHome is null ? WorkspaceProductionGate.Read(dataHome)
        : SyntheticHome(dataHome) ? _fixtureBinding is null ? "SYNTHETIC" : _fixtureBinding() : null;

    private static HashSet<string> Eligible(WorkspaceMetadataPlan plan)
    {
        Require(plan.Sessions.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).Count() == plan.Sessions.Count, "SYNC_SESSION_PROOF_INVALID");
        var ids = plan.ReferencesToRemove.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
        Require(ids.All(id => plan.Sessions.Any(x => x.SessionId == id && x.EligibleForProposal)), "SYNC_SESSION_PROOF_INVALID");
        return ids;
    }

    private static void WriteNew(string root, string path, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireMissing(root, path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(true);
    }

    private static void RequireMissing(string root, string path) => Require(new PathGuard().Check(root, path).Status == PathCheckStatus.Missing, "SYNC_ARTIFACT_EXISTS_OR_UNSAFE");
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string code) { if (!condition) throw new SyncBlockedException(code); }
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException;
    internal sealed class SyncBlockedException(string code) : Exception { internal string Code { get; } = code; }
    private sealed record WriteIntent(int SchemaVersion, Guid OperationId, Guid CleanupPlanId, string RootFingerprint,
        WorkspaceFileIdentity Source, string CandidateSha256, DateTimeOffset AttemptUtc, string BackupManifestSha256, string BackupVerificationSha256);
}

internal enum WorkspaceWritePoint { IntentFlushed, CandidateFlushed, BeforeReplace, Replaced, Verified, CommitWindow, OriginalRetained, OutcomeRecorded }
