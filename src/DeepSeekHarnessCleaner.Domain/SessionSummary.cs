namespace DeepSeekHarnessCleaner.Domain;

/// <summary>解析状态（design.md §1 ParseStatus）。T-10 起归位 Domain 层供各层共用。</summary>
public enum ParseStatus
{
    Valid,
    Partial,
    Unsupported,
    Corrupt,
    Unreadable,
}

/// <summary>日期来源（design.md §1/§3）。</summary>
public enum DateOrigin
{
    Header,
    Event,
    FileMtime,
    Unknown,
}

/// <summary>单个物理日志文件（design.md §1）。Sha256 由后续哈希任务填充，可为 null。</summary>
public sealed record PhysicalLog(
    string FullPath,
    int Generation,
    bool Compressed,
    long Bytes,
    DateTimeOffset ModifiedAt,
    string? Sha256,
    ParseStatus Status);

/// <summary>
/// 一条逻辑会话库存记录（design.md §1/§2，T-10）。多代际归并为一条；
/// SessionId 仅在全部有效 header 一致且目录身份映射成立时非空；
/// IsAmbiguous 标记同代双编码或 header 身份冲突（阻止清理）。
/// </summary>
public sealed record SessionSummary(
    string? SessionId,
    string? Project,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastActivityAt,
    DateOrigin DateOrigin,
    IReadOnlyList<PhysicalLog> Generations,
    ParseStatus Status)
{
    /// <summary>该会话全部物理文件字节求和（可解释的容量统计）。</summary>
    public long TotalBytes { get; init; }

    public bool IsAmbiguous { get; init; }

    /// <summary>Latest verified durable session/title event, if available.</summary>
    public string? Title { get; init; }

    /// <summary>Verified workspace archive membership; null when metadata cannot be verified.</summary>
    public bool? IsArchived { get; init; }
}

/// <summary>不属于任何会话目录的异常文件（非白名单/位置异常/访问受阻）。</summary>
public sealed record AnomalousFile(string FullPath, string? RelativePath, string Reason, long? Bytes);

/// <summary>扫描源返回的单文件事实：物理属性 + 头部验证结果（供归并，不含正文）。</summary>
public sealed record ScannedLogFile(
    PhysicalLog Log,
    string? HeaderSessionId,
    DateTimeOffset? HeaderCreatedAt,
    bool IdentityMismatch)
{
    public string? Title { get; init; }
}

/// <summary>扫描源返回的单个会话目录（同一目录 = 同一候选会话，含多代际）。</summary>
public sealed record ScannedSessionDirectory(
    string RelativeDirectory,
    IReadOnlyList<ScannedLogFile> Files,
    IReadOnlyList<int> ConflictingGenerations)
{
    public bool? IsArchived { get; init; }
}

/// <summary>扫描源整体输出；RootErrorCode 非空表示根不可用。</summary>
public sealed record SessionScanData(
    string? RootErrorCode,
    IReadOnlyList<ScannedSessionDirectory> Sessions,
    IReadOnlyList<AnomalousFile> AnomalousFiles);
