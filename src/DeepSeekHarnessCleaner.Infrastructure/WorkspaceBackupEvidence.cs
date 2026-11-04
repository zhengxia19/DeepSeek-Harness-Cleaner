namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Local backup verification facts, not a write permit. Re-verify files immediately before using them.</summary>
public sealed record WorkspaceBackupEvidence(Guid OperationId, string BackupRoot, string SourcePath, string SourceSha256,
    long SourceLength, string RawPath, string ManifestPath, string VerificationPath, string ManifestSha256,
    string VerificationSha256, DateTimeOffset CreatedUtc, DateTimeOffset VerifiedUtc);

public sealed record WorkspaceBackupResult(WorkspaceBackupEvidence? Backup, string? ErrorCode)
{
    public bool Success => Backup is not null;
}
