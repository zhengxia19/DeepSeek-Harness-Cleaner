namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// Local backup verification facts, not a write permit. Re-verify files immediately before using them.
/// B07：构造器 internal——"备份校验通过"的事实只能由 WorkspaceBackupService.Create 产生；
/// 公开构造会让任意 DTO 状态伪造证据进入写链（对齐 WorkspaceWritePreparation 能力对象模式）。
/// </summary>
public sealed record WorkspaceBackupEvidence
{
    internal WorkspaceBackupEvidence(Guid operationId, string backupRoot, string sourcePath, string sourceSha256,
        long sourceLength, string rawPath, string manifestPath, string verificationPath, string manifestSha256,
        string verificationSha256, DateTimeOffset createdUtc, DateTimeOffset verifiedUtc)
        => (OperationId, BackupRoot, SourcePath, SourceSha256, SourceLength, RawPath, ManifestPath,
            VerificationPath, ManifestSha256, VerificationSha256, CreatedUtc, VerifiedUtc)
            = (operationId, backupRoot, sourcePath, sourceSha256, sourceLength, rawPath, manifestPath,
                verificationPath, manifestSha256, verificationSha256, createdUtc, verifiedUtc);

    public Guid OperationId { get; init; }
    public string BackupRoot { get; init; }
    public string SourcePath { get; init; }
    public string SourceSha256 { get; init; }
    public long SourceLength { get; init; }
    public string RawPath { get; init; }
    public string ManifestPath { get; init; }
    public string VerificationPath { get; init; }
    public string ManifestSha256 { get; init; }
    public string VerificationSha256 { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset VerifiedUtc { get; init; }
}

public sealed record WorkspaceBackupResult(WorkspaceBackupEvidence? Backup, string? ErrorCode)
{
    public bool Success => Backup is not null;
}
