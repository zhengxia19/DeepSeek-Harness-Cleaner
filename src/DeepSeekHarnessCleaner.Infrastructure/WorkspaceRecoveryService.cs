using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

public enum WorkspaceRecoveryState { Blocked, OriginalUnchanged, OriginalRetained, AppliedVerified, ResolvedOriginal, ResolvedApplied }
public sealed record WorkspaceRecoveryResult(WorkspaceRecoveryState State, string? ErrorCode, DateTimeOffset ObservedUtc)
{
    public bool Resolved => State is WorkspaceRecoveryState.ResolvedOriginal or WorkspaceRecoveryState.ResolvedApplied;
}

/// <summary>Explicit reconciliation of one known operation. Never overwrites a new file or restores deleted logs.</summary>
public sealed class WorkspaceRecoveryService
{
    private readonly HarnessProcessProbe _probe;
    private readonly WorkspaceMetadataWriter _scope;
    private readonly Action<WorkspaceRecoveryPoint>? _fault;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    public WorkspaceRecoveryService(HarnessProcessProbe? probe = null)
    {
        _probe = probe ?? new HarnessProcessProbe();
        _scope = new WorkspaceMetadataWriter(_probe);
    }
    internal WorkspaceRecoveryService(string fixtureHome, HarnessProcessProbe probe, Action<WorkspaceRecoveryPoint>? fault = null)
    {
        _probe = probe;
        _scope = new WorkspaceMetadataWriter(fixtureHome, probe);
        _fault = fault;
    }

    public WorkspaceRecoveryResult Inspect(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct = default)
    {
        try
        {
            Check(plan, backup, ct);
            using var files = PinBackup(plan, backup);
            using var state = Capture(plan, backup, ct);
            return new(state.State, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { return Failure("SYNC_RECOVERY_CANCELED"); }
        catch (Exception ex) when (Expected(ex)) { return Failure("SYNC_RECOVERY_UNVERIFIABLE"); }
    }

    /// <summary>Reinspect actual materials, then recover only an absent original or acknowledge an exact applied tree.</summary>
    public WorkspaceRecoveryResult Resolve(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct = default)
    {
        try
        {
            Check(plan, backup, ct);
            var directory = Path.GetDirectoryName(plan.WorkspacePath)!;
            var operationRoot = Path.GetDirectoryName(backup.RawPath)!;
            using var sourcePins = WorkspaceFileIdentity.PinDirectories(directory);
            using var backupPins = WorkspaceFileIdentity.PinDirectories(operationRoot);
            using var files = PinBackup(plan, backup);
            using var state = Capture(plan, backup, ct);
            if (state.State is WorkspaceRecoveryState.ResolvedOriginal or WorkspaceRecoveryState.ResolvedApplied)
                return new(state.State, null, DateTimeOffset.UtcNow);
            _fault?.Invoke(WorkspaceRecoveryPoint.Rechecked);
            ct.ThrowIfCancellationRequested();
            Require(_probe.Probe().AllowsWrite);
            if (state.State == WorkspaceRecoveryState.OriginalRetained)
            {
                Require(new PathGuard().Check(plan.DataHome, plan.WorkspacePath).Status == PathCheckStatus.Missing);
                WorkspaceFileIdentity.RenameNoReplace(state.Previous!, plan.WorkspacePath);
                _fault?.Invoke(WorkspaceRecoveryPoint.OriginalRestored);
            }
            var finalState = state.State == WorkspaceRecoveryState.AppliedVerified
                ? WorkspaceRecoveryState.ResolvedApplied : WorkspaceRecoveryState.ResolvedOriginal;
            using (var current = OpenRead(plan.WorkspacePath))
                Require(WorkspaceFileIdentity.Read(current).Sha256 == (finalState == WorkspaceRecoveryState.ResolvedApplied ? state.Intent!.CandidateSha256 : plan.SourceSha256));
            // Quarantine the exact held candidate and marker; do not delete or follow their names after verification.
            if (state.Candidate is not null)
            {
                Require(_probe.Probe().AllowsWrite);
                WorkspaceFileIdentity.RenameNoReplace(state.Candidate, Path.Combine(operationRoot, "recovery-candidate.json"));
                _fault?.Invoke(WorkspaceRecoveryPoint.CandidateRetained);
            }
            var recordPath = Path.Combine(operationRoot, "recovery-resolved.json");
            var record = new Resolution(1, plan.OperationId, plan.CleanupPlanId, finalState,
                finalState == WorkspaceRecoveryState.ResolvedApplied ? state.Intent!.CandidateSha256 : plan.SourceSha256!,
                backup.ManifestSha256, backup.VerificationSha256);
            if (Exists(operationRoot, recordPath)) Require(Read<Resolution>(recordPath, 16 * 1024) == record);
            else WriteNew(operationRoot, recordPath, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            _fault?.Invoke(WorkspaceRecoveryPoint.RecordFlushed);
            Require(Read<Resolution>(recordPath, 16 * 1024) == record);
            Require(_probe.Probe().AllowsWrite);
            WorkspaceFileIdentity.RenameNoReplace(state.Marker!, Path.Combine(operationRoot, "recovery-lock.txt"));
            return new(finalState, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { return Failure("SYNC_RECOVERY_CANCELED"); }
        catch (Exception ex) when (Expected(ex)) { return Failure("SYNC_RECOVERY_UNVERIFIABLE"); }
    }

    private void Check(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Require(_scope.AllowsScope(plan.DataHome) && _probe.Probe().AllowsWrite);
        Require(plan.WorkspacePath == Path.Combine(Path.GetFullPath(plan.DataHome), "storages", "workspace.json")
            && RootGuard.TryRootFingerprint(Path.Combine(plan.DataHome, "sessions")) == plan.RootFingerprint);
        Require(new WorkspaceBackupService(_probe).Verify(plan, backup, ct).Success);
    }

    private static RecoveryCapture Capture(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup, CancellationToken ct)
    {
        var capture = new RecoveryCapture();
        try
        {
            var root = Path.GetDirectoryName(backup.RawPath)!;
            var sourceRoot = Path.GetDirectoryName(plan.WorkspacePath)!;
            capture.Source = capture.Open(plan.DataHome, plan.WorkspacePath);
            capture.Previous = capture.Open(root, Path.Combine(root, "workspace.replaced.json"));
            capture.Candidate = capture.Open(plan.DataHome, Path.Combine(sourceRoot, ".dshcleaner-workspace-" + plan.OperationId.ToString("N") + ".tmp"));
            capture.Marker = capture.Open(plan.DataHome, Path.Combine(sourceRoot, ".dshcleaner-workspace-sync.lock"));
            var marker = capture.Marker;
            if (marker is not null)
            {
                Require(marker.Length == 32);
                marker.Position = 0;
                var markerBytes = new byte[32];
                marker.ReadExactly(markerBytes);
                Require(Encoding.UTF8.GetString(markerBytes) == plan.OperationId.ToString("N"));
            }
            var source = capture.Source is null ? null : WorkspaceFileIdentity.Read(capture.Source);
            var previous = capture.Previous is null ? null : WorkspaceFileIdentity.Read(capture.Previous);
            var candidate = capture.Candidate is null ? null : WorkspaceFileIdentity.Read(capture.Candidate);
            var intentPath = Path.Combine(root, "write-intent.json");
            Intent? intent = null;
            if (Exists(root, intentPath))
            {
                intent = Read<Intent>(intentPath, 64 * 1024);
                Require(intent.SchemaVersion == 1 && intent.OperationId == plan.OperationId && intent.CleanupPlanId == plan.CleanupPlanId
                    && intent.RootFingerprint == plan.RootFingerprint && intent.Source.Sha256 == plan.SourceSha256 && intent.Source.Length == backup.SourceLength
                    && intent.BackupManifestSha256 == backup.ManifestSha256 && intent.BackupVerificationSha256 == backup.VerificationSha256
                    && intent.AttemptUtc != default && intent.CandidateSha256.Length == 64);
                var ids = plan.ReferencesToRemove.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
                Require(ids.Count > 0 && ids.All(id => plan.Sessions.Count(x => x.SessionId == id && x.EligibleForProposal) == 1));
                var bytes = WorkspaceMetadataTransform.Build(WorkspaceMetadataTransform.Load(backup.RawPath), ids, intent.AttemptUtc);
                Require(Hash(bytes) == intent.CandidateSha256);
                if (candidate is not null) Require(candidate.Sha256 == intent.CandidateSha256);
                if (previous is not null) Require(previous == intent.Source);
            }
            else Require(candidate is null && previous is null);
            capture.Intent = intent;
            if (source?.Sha256 == plan.SourceSha256)
            {
                if (intent is not null) Require(source == intent.Source);
                Require(previous is null);
                capture.State = WorkspaceRecoveryState.OriginalUnchanged;
            }
            else if (source is null && previous is not null && intent is not null)
                capture.State = WorkspaceRecoveryState.OriginalRetained;
            else
            {
                Require(source is not null && intent is not null && source.Sha256 == intent.CandidateSha256 && previous is not null && candidate is null);
                WorkspaceMetadataWriter.CheckPhysical(plan, plan.ReferencesToRemove.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal), ct);
                capture.State = WorkspaceRecoveryState.AppliedVerified;
            }
            // A prior partial recovery may already have quarantined the candidate; its hash must still match.
            var savedCandidate = Path.Combine(root, "recovery-candidate.json");
            if (Exists(root, savedCandidate))
            {
                using var held = OpenRead(savedCandidate);
                Require(candidate is null && intent is not null && WorkspaceFileIdentity.Read(held).Sha256 == intent.CandidateSha256);
            }
            var resolvedPath = Path.Combine(root, "recovery-resolved.json");
            if (Exists(root, resolvedPath))
            {
                var resolved = Read<Resolution>(resolvedPath, 16 * 1024);
                var expected = capture.State == WorkspaceRecoveryState.AppliedVerified ? WorkspaceRecoveryState.ResolvedApplied : WorkspaceRecoveryState.ResolvedOriginal;
                Require(resolved == new Resolution(1, plan.OperationId, plan.CleanupPlanId, expected, source!.Sha256,
                    backup.ManifestSha256, backup.VerificationSha256));
                if (marker is null)
                {
                    var savedMarkerPath = Path.Combine(root, "recovery-lock.txt");
                    Require(Exists(root, savedMarkerPath));
                    using var savedMarker = WorkspaceFileIdentity.OpenForRename(savedMarkerPath);
                    _ = WorkspaceFileIdentity.Read(savedMarker);
                    Require(savedMarker.Length == 32);
                    savedMarker.Position = 0;
                    Require(new StreamReader(savedMarker, Encoding.UTF8, leaveOpen: true).ReadToEnd() == plan.OperationId.ToString("N"));
                    capture.State = expected;
                }
            }
            else if (marker is null && capture.State == WorkspaceRecoveryState.AppliedVerified)
            {
                // A process can exit after retiring its lock but before returning Applied to its caller.
                // Accept only the exact durable completion record plus the already revalidated result/original/proofs.
                var appliedPath = Path.Combine(root, "write-applied.json");
                Require(Exists(root, appliedPath));
                var applied = Read<AppliedRecord>(appliedPath, 16 * 1024);
                Require(applied.OperationId == plan.OperationId && applied.CandidateHash == intent!.CandidateSha256
                    && applied.AppliedUtc == intent.AttemptUtc);
                capture.State = WorkspaceRecoveryState.ResolvedApplied;
            }
            Require(marker is not null || capture.State is WorkspaceRecoveryState.ResolvedOriginal or WorkspaceRecoveryState.ResolvedApplied);
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    private static BackupPins PinBackup(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence backup)
    {
        var pins = new BackupPins();
        try
        {
            foreach (var item in new[] { (backup.RawPath, plan.SourceSha256), (backup.ManifestPath, backup.ManifestSha256), (backup.VerificationPath, backup.VerificationSha256) })
            {
                var held = WorkspaceFileIdentity.OpenPinnedRead(item.Item1);
                pins.Streams.Add(held);
                Require(WorkspaceFileIdentity.Read(held).Sha256 == item.Item2);
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class BackupPins : IDisposable
    {
        internal List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams) stream.Dispose(); }
    }

    private sealed class RecoveryCapture : IDisposable
    {
        private readonly List<FileStream> _streams = [];
        internal FileStream? Source, Previous, Candidate, Marker;
        internal Intent? Intent;
        internal WorkspaceRecoveryState State;
        internal FileStream? Open(string root, string path)
        {
            if (!Exists(root, path)) return null;
            var stream = WorkspaceFileIdentity.OpenForRename(path);
            _streams.Add(stream);
            _ = WorkspaceFileIdentity.Read(stream);
            return stream;
        }
        public void Dispose() { foreach (var stream in _streams) stream.Dispose(); }
    }

    private static bool Exists(string root, string path)
    {
        var check = new PathGuard().Check(root, path);
        Require(check.IsAllowed || check.Status == PathCheckStatus.Missing);
        return check.IsAllowed;
    }
    private static T Read<T>(string path, int limit)
    {
        using var stream = WorkspaceFileIdentity.OpenForRename(path);
        _ = WorkspaceFileIdentity.Read(stream);
        Require(stream.Length <= limit);
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        Require(WorkspaceJson.DuplicateField(doc.RootElement) is null);
        return doc.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException();
    }
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    private static void WriteNew(string root, string path, byte[] bytes)
    {
        Require(!Exists(root, path));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("SYNC_RECOVERY_UNVERIFIABLE"); }
    private static WorkspaceRecoveryResult Failure(string code) => new(WorkspaceRecoveryState.Blocked, code, DateTimeOffset.UtcNow);
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or JsonException or System.Security.SecurityException or NotSupportedException or WorkspaceMetadataWriter.SyncBlockedException;
    private sealed record Intent(int SchemaVersion, Guid OperationId, Guid CleanupPlanId, string RootFingerprint,
        WorkspaceFileIdentity Source, string CandidateSha256, DateTimeOffset AttemptUtc, string BackupManifestSha256, string BackupVerificationSha256);
    private sealed record Resolution(int SchemaVersion, Guid OperationId, Guid CleanupPlanId, WorkspaceRecoveryState State,
        string SourceSha256, string BackupManifestSha256, string BackupVerificationSha256);
    private sealed record AppliedRecord(Guid OperationId, string CandidateHash, DateTimeOffset AppliedUtc);
}

internal enum WorkspaceRecoveryPoint { Rechecked, OriginalRestored, CandidateRetained, RecordFlushed }
