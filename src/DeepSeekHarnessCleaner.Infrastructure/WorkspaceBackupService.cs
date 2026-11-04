using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Mandatory independent raw Workspace backup; never rewrites source, never accepts a session-ZIP bypass.</summary>
public sealed class WorkspaceBackupService
{
    private const string RawName = "workspace.original.json";
    private const string ManifestName = "manifest.json";
    private const string VerificationName = "verification.json";
    private readonly HarnessProcessProbe _probe;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public WorkspaceBackupService(HarnessProcessProbe? probe = null) => _probe = probe ?? new HarnessProcessProbe();

    public WorkspaceBackupResult Create(WorkspaceMetadataPlan plan, string backupRoot, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidatePlan(plan, creating: true);
            if (!_probe.Probe().AllowsWrite) return Fail("WORKSPACE_BACKUP_PROCESS_NOT_INACTIVE");
            var paths = Paths(plan, backupRoot);
            var source = new WorkspaceReferenceReader().Read(plan.DataHome);
            if (source.Status != WorkspaceReferenceStatus.Known || source.SnapshotSha256 != plan.SourceSha256)
                return Fail("WORKSPACE_BACKUP_SOURCE_CHANGED");
            if (Directory.Exists(paths.Directory)) return Verify(plan, backupRoot, ct);
            if (File.Exists(paths.Directory)) return Fail("WORKSPACE_BACKUP_TARGET_EXISTS");
            var info = new FileInfo(plan.WorkspacePath);
            var modified = info.LastWriteTimeUtc;
            var raw = ReadBounded(plan.WorkspacePath, WorkspaceJson.MaxBytes, ct);
            info.Refresh();
            if (Hash(raw) != plan.SourceSha256 || info.Length != raw.Length || info.LastWriteTimeUtc != modified)
                return Fail("WORKSPACE_BACKUP_SOURCE_CHANGED");
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(paths.Directory);
            CheckRoot(paths.Directory);
            WriteNew(paths.Raw, raw, paths.Directory, ct);
            var manifest = new Manifest(1, plan.OperationId, plan.CleanupPlanId, Path.GetFullPath(plan.WorkspacePath),
                plan.SourceSha256!, raw.Length, new DateTimeOffset(modified, TimeSpan.Zero), DateTimeOffset.UtcNow);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            WriteNew(paths.Manifest, manifestBytes, paths.Directory, ct);
            if (Hash(ReadBounded(paths.Raw, WorkspaceJson.MaxBytes, ct)) != plan.SourceSha256)
                return Fail("WORKSPACE_BACKUP_VERIFY_FAILED");
            var record = new Verification(1, plan.OperationId, plan.SourceSha256!, raw.Length, Hash(manifestBytes), DateTimeOffset.UtcNow);
            WriteNew(paths.Verification, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), paths.Directory, ct);
            return Verify(plan, backupRoot, ct);
        }
        catch (OperationCanceledException) { return Fail("WORKSPACE_BACKUP_CANCELED"); }
        catch (Exception ex) when (IsExpected(ex)) { return Fail("WORKSPACE_BACKUP_CREATE_FAILED"); }
        // Partial files are intentionally retained for inspection; no speculative cleanup or overwrite.
    }

    /// <summary>Validate the original backup independently of current source bytes, so recovery remains possible.</summary>
    public WorkspaceBackupResult Verify(WorkspaceMetadataPlan plan, string backupRoot, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidatePlan(plan, creating: false);
            var paths = Paths(plan, backupRoot);
            CheckRoot(paths.Directory);
            foreach (var file in new[] { paths.Raw, paths.Manifest, paths.Verification })
                if (!new PathGuard().Check(paths.Directory, file).IsAllowed) return Fail("WORKSPACE_BACKUP_PATH_REJECTED");
            var raw = ReadBounded(paths.Raw, WorkspaceJson.MaxBytes, ct);
            var manifestBytes = ReadBounded(paths.Manifest, 256 * 1024, ct);
            var recordBytes = ReadBounded(paths.Verification, 16 * 1024, ct);
            var manifest = Parse<Manifest>(manifestBytes);
            var record = Parse<Verification>(recordBytes);
            if (manifest.SchemaVersion != 1 || record.SchemaVersion != 1 || manifest.OperationId != plan.OperationId
                || record.OperationId != plan.OperationId || manifest.CleanupPlanId != plan.CleanupPlanId
                || !Same(manifest.SourcePath, plan.WorkspacePath) || manifest.SourceSha256 != plan.SourceSha256
                || record.SourceSha256 != plan.SourceSha256 || manifest.SourceLength != raw.Length || record.SourceLength != raw.Length
                || Hash(raw) != plan.SourceSha256 || record.ManifestSha256 != Hash(manifestBytes)
                || manifest.CreatedUtc == default || manifest.SourceFileLastWriteUtc == default || record.VerifiedUtc == default)
                return Fail("WORKSPACE_BACKUP_VERIFY_FAILED");
            return new(new WorkspaceBackupEvidence(plan.OperationId, Path.GetFullPath(backupRoot), manifest.SourcePath,
                manifest.SourceSha256, raw.Length, paths.Raw, paths.Manifest, paths.Verification, Hash(manifestBytes),
                Hash(recordBytes), manifest.CreatedUtc, record.VerifiedUtc), null);
        }
        catch (OperationCanceledException) { return Fail("WORKSPACE_BACKUP_CANCELED"); }
        catch (Exception ex) when (IsExpected(ex)) { return Fail("WORKSPACE_BACKUP_VERIFY_FAILED"); }
    }

    /// <summary>Recheck expected manifest/verification identity too, not just caller-provided positive flags.</summary>
    public WorkspaceBackupResult Verify(WorkspaceMetadataPlan plan, WorkspaceBackupEvidence expected, CancellationToken ct = default)
    {
        var result = Verify(plan, expected.BackupRoot, ct);
        return result.Backup is { } actual && actual.OperationId == expected.OperationId && actual.SourcePath == expected.SourcePath
            && actual.SourceSha256 == expected.SourceSha256 && actual.SourceLength == expected.SourceLength
            && actual.RawPath == expected.RawPath && actual.ManifestPath == expected.ManifestPath && actual.VerificationPath == expected.VerificationPath
            && actual.ManifestSha256 == expected.ManifestSha256 && actual.VerificationSha256 == expected.VerificationSha256
            ? result : Fail(result.ErrorCode ?? "WORKSPACE_BACKUP_EVIDENCE_CHANGED");
    }

    private static (string Directory, string Raw, string Manifest, string Verification) Paths(WorkspaceMetadataPlan plan, string backupRoot)
    {
        ValidateDestination(plan.DataHome, backupRoot);
        var directory = Path.Combine(Path.GetFullPath(backupRoot), "workspace-sync-" + plan.OperationId.ToString("N"));
        if (Within(directory, plan.DataHome)) throw new IOException("WORKSPACE_BACKUP_INSIDE_DATA_ROOT");
        if (OperatingSystem.IsWindows())
        {
            var dsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop");
            if (Within(directory, dsh) || Within(directory, desktop)) throw new IOException("WORKSPACE_BACKUP_PROTECTED_DESTINATION");
        }
        return (directory, Path.Combine(directory, RawName), Path.Combine(directory, ManifestName), Path.Combine(directory, VerificationName));
    }

    internal static void ValidateDestination(string dataHome, string backupRoot)
    {
        CheckRoot(backupRoot);
        if (Within(backupRoot, dataHome)) throw new IOException("WORKSPACE_BACKUP_INSIDE_DATA_ROOT");
        if (OperatingSystem.IsWindows())
        {
            var dsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "@deepseek-ai", "dsh-desktop");
            if (Within(backupRoot, dsh) || Within(backupRoot, desktop)) throw new IOException("WORKSPACE_BACKUP_PROTECTED_DESTINATION");
        }
    }

    private static void ValidatePlan(WorkspaceMetadataPlan plan, bool creating)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if ((creating && (plan.State != WorkspaceSyncState.Proposed || plan.ReferencesToRemove.Count == 0)) || plan.OperationId == Guid.Empty
            || plan.CleanupPlanId == Guid.Empty || plan.SourceSha256 is not { Length: 64 } hash
            || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || !Path.IsPathFullyQualified(plan.DataHome) || !Same(plan.WorkspacePath, Path.Combine(plan.DataHome, "storages", "workspace.json")))
            throw new ArgumentException("WORKSPACE_BACKUP_PLAN_INVALID");
    }

    private static void CheckRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)
            || root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x == ".." || x.EndsWith(' ') || (x != "." && x.EndsWith('.')))
            || new PathGuard().Check(root, root).ErrorCode != PathGuard.PathEscapeErrorCode)
            throw new IOException("WORKSPACE_BACKUP_ROOT_REJECTED");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("WORKSPACE_BACKUP_REPARSE");
    }

    private static byte[] ReadBounded(string path, int limit, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("WORKSPACE_BACKUP_SIZE_LIMIT");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length + count > limit) throw new InvalidDataException("WORKSPACE_BACKUP_SIZE_LIMIT");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static void WriteNew(string path, byte[] bytes, string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckRoot(root);
        var check = new PathGuard().Check(root, path);
        if (check.Status != PathCheckStatus.Missing) throw new IOException("WORKSPACE_BACKUP_TARGET_EXISTS");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static T Parse<T>(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || WorkspaceJson.DuplicateField(doc.RootElement) is not null)
            throw new JsonException("WORKSPACE_BACKUP_MANIFEST_INVALID");
        return doc.Deserialize<T>(JsonOptions) ?? throw new JsonException("WORKSPACE_BACKUP_MANIFEST_INVALID");
    }

    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException or JsonException;
    private static WorkspaceBackupResult Fail(string code) => new(null, code);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Same(string left, string right) => Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Within(string path, string root) => Same(path, root)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Manifest(int SchemaVersion, Guid OperationId, Guid CleanupPlanId, string SourcePath,
        string SourceSha256, long SourceLength, DateTimeOffset SourceFileLastWriteUtc, DateTimeOffset CreatedUtc);
    private sealed record Verification(int SchemaVersion, Guid OperationId, string SourceSha256, long SourceLength,
        string ManifestSha256, DateTimeOffset VerifiedUtc);
}
