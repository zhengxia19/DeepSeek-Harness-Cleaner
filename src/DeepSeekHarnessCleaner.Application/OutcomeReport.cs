using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>报告项状态（与执行器逐项状态一一对应；UI 层映射，Application 不依赖 Infrastructure）。</summary>
public enum OutcomeItemStatus
{
    Deleted,
    Skipped,
    Failed,
}

/// <summary>
/// 报告单项（REQ-016）：UI 展示用。<see cref="DisplayPath"/> 仅出现在 UI，
/// 绝不写入 <see cref="OutcomeReport.BuildLog"/> 生成的脱敏日志。
/// </summary>
public sealed record OutcomeItem(
    string DisplayPath,
    CleanupCategory Category,
    string? OwnerSessionId,
    OutcomeItemStatus Status,
    string? ErrorCode,
    long FreedBytes);

/// <summary>报告输入：由 UI 层从执行结果 + 计划 + 备份事实组装（纯内存，零网络）。</summary>
public sealed record OutcomeReportInput(
    bool Aborted,
    string? AbortCode,
    string? BackupZipPath,
    string? BackupSignature,
    int BackupEntryCount,
    long BackupTotalBytes,
    IReadOnlyList<OutcomeItem> Items)
{
    public PostCleanupAudit? SessionAudit { get; init; }
    public WorkspaceSyncOutcome? WorkspaceSync { get; init; }
}

/// <summary>
/// T-31 不泄露正文的执行报告（REQ-016，TC-16/TC-18）：
/// UI 显示每项成功/跳过/失败、实际释放字节、备份位置与错误码；
/// <see cref="BuildLog"/> 产出脱敏结构化日志——只含固定事件名、白名单 token、类别、状态与数值，
/// 路径、会话正文、自由文本与任何不匹配白名单的 token 一律不进入日志。
/// </summary>
public sealed class OutcomeReport
{
    /// <summary>会话 ID 白名单（文件名派生 ID 形态）；不匹配则日志记 "-"。</summary>
    private static readonly Regex SafeSessionId = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>错误码白名单（大写字母/数字/下划线/点/冒号/连字符，如 GATE_BACKUP_INVALID:SEC-03）。</summary>
    private static readonly Regex SafeErrorCode = new("^[A-Za-z0-9_.:-]{1,80}$", RegexOptions.CultureInvariant);

    /// <summary>备份 ZIP 文件名白名单（仅文件名，不含目录）。</summary>
    private static readonly Regex SafeZipName = new(@"^[A-Za-z0-9._-]{1,128}\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>清单签名白名单：SHA-256 小写 hex。</summary>
    private static readonly Regex SafeSignature = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    // Closed code vocabulary for new audit events: never log free text, paths, IDs, or credentials disguised as codes.
    private static readonly HashSet<string> AuditLogCodes = new(StringComparer.Ordinal)
    {
        ErrorCodes.PostScanFailed, ErrorCodes.PostUiScanFailed, ErrorCodes.PostPreAuditMissing, ErrorCodes.PostAuditPlanMismatch,
        ErrorCodes.PostAuditRootInvalid, ErrorCodes.PostAuditRootMismatch, ErrorCodes.PostAuditRootUnavailable, ErrorCodes.PostAuditReadFailed,
        ErrorCodes.PostTargetAssociationUnknown, ErrorCodes.PostTargetUnknown, ErrorCodes.PostTargetChangedType, ErrorCodes.PostTargetUnreadable,
        ErrorCodes.ReportPostAuditPlanMismatch, ErrorCodes.WorkspaceMissing, ErrorCodes.WorkspaceReadFailed, ErrorCodes.WorkspaceJsonInvalid,
        ErrorCodes.WorkspaceVersionUnsupported, ErrorCodes.WorkspacePerRecordUnsupported, ErrorCodes.WorkspaceLayoutAmbiguous,
        ErrorCodes.WorkspaceDuplicateField, ErrorCodes.WorkspaceNotInitialized, ErrorCodes.WorkspaceTransactionUnknown,
        ErrorCodes.WorkspaceIdsInvalid, ErrorCodes.WorkspaceRegistryMismatch, ErrorCodes.WorkspaceMemberAmbiguous,
        ErrorCodes.WorkspaceArchivePinConflict, ErrorCodes.WorkspaceRecordInvalid, ErrorCodes.WorkspaceTooLarge,
        ErrorCodes.AttachmentReferenceCoverageUnknown, ErrorCodes.AttachmentSessionCoverageUnknown, ErrorCodes.ReferenceUnreadable,
        ErrorCodes.ReferenceDecodeIncomplete, ErrorCodes.ReferenceReadLimit, ErrorCodes.AttachmentChanged, ErrorCodes.AttachmentRootMissing,
        ErrorCodes.AttachmentFileMissing, ErrorCodes.AttachmentRelationUnknown, ErrorCodes.AuditRelationUnknown,
        ErrorCodes.SessionIdentityUnknown, ErrorCodes.SessionIdentityDuplicate, ErrorCodes.LogPathRejected, ErrorCodes.LogUnreadable,
        ErrorCodes.PathReparse, ErrorCodes.PathEscape, ErrorCodes.RootInvalid, ErrorCodes.RootUnreadable, ErrorCodes.FileAccessDenied,
        ErrorCodes.CacheStructureMismatch, ErrorCodes.CacheNameCollision, ErrorCodes.CacheIdNotInInventory, ErrorCodes.CacheInventoryMissing,
        ErrorCodes.NotWhitelisted, ErrorCodes.UnexpectedLocation, ErrorCodes.Missing,
    };

    public OutcomeReport(OutcomeReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.WorkspaceSync is { } sync && (sync.ProposedReferenceCount < 0 || sync.RemovedReferenceCount < 0
            || sync.RemovedReferenceCount > sync.ProposedReferenceCount
            || sync.State == WorkspaceSyncState.Applied && (sync.RemovedReferenceCount is not > 0 || sync.BackupDirectory is null)
            || sync.State == WorkspaceSyncState.RecoveryRequired && sync.RemovedReferenceCount is not null))
            input = input with { WorkspaceSync = sync with { State = WorkspaceSyncState.RecoveryRequired, ErrorCode = "SYNC_REPORT_STATE_INVALID", RemovedReferenceCount = null } };
        Input = input;
        Items = input.Items;
        Aborted = input.Aborted;
        AbortCode = input.AbortCode;
        BackupZipPath = input.BackupZipPath;
        BackupSignature = input.BackupSignature;
        BackupEntryCount = Math.Max(0, input.BackupEntryCount);
        BackupTotalBytes = Math.Max(0, input.BackupTotalBytes);
        Sessions = input.SessionAudit?.Sessions.Select(x => ProjectSession(x,
            input.SessionAudit.After is null || input.SessionAudit.After.HasUnknowns || input.SessionAudit.Issues.Count > 0)).ToArray() ?? [];
        var issues = input.SessionAudit is { } audit
            ? audit.Issues.Concat(audit.After?.Issues ?? []).Concat(RelationIssues(audit.After)).Concat(audit.Targets.Where(x => x.Presence == TargetPresence.Unknown)
                .Select(x => new SessionResidueIssue(x.Reason ?? "POST_TARGET_UNKNOWN", x.AbsolutePath, null)))
            : Array.Empty<SessionResidueIssue>();
        AuditIssues = issues.Distinct().ToArray();
    }

    public OutcomeReportInput Input { get; }
    public WorkspaceSyncOutcome? WorkspaceSync => Input.WorkspaceSync;
    public string WorkspaceSyncSummary => WorkspaceSync is not { } sync ? "Workspace 同步：未提供结果。" :
        "Workspace 同步：" + (sync.State switch
        {
            WorkspaceSyncState.NotRequested => "未请求，未写入元数据",
            WorkspaceSyncState.NoOp => "本次无需变更",
            WorkspaceSyncState.Applied => $"已验证移除 {sync.RemovedReferenceCount} 处引用",
            WorkspaceSyncState.RecoveryRequired => "结果待恢复复查，移除数量未知；请保留已有材料",
            WorkspaceSyncState.Blocked => "已阻断",
            _ => "状态未知",
        }) + (sync.ErrorCode is null ? "。" : $"（{sync.ErrorCode}）。")
        + "文件删除与元数据同步分别执行，不构成原子事务。";
    public IReadOnlyList<OutcomeSessionRow> Sessions { get; }
    public IReadOnlyList<SessionResidueIssue> AuditIssues { get; }
    public bool HasSessionAudit => Input.SessionAudit is not null;
    public int PhysicalResidueSessionCount => Sessions.Count(x => x.State == SessionCleanupState.PhysicalResidue);
    public int MetadataResidueSessionCount => Sessions.Count(x => x.WorkspaceReferenceCount > 0);
    public int UnknownSessionCount => Sessions.Count(x => x.HasUnknowns || x.State == SessionCleanupState.Unknown);
    public bool HasAuditUnknowns => HasSessionAudit && (Input.SessionAudit!.HasUnknowns || UnknownSessionCount > 0 || AuditIssues.Count > 0);

    public IReadOnlyList<OutcomeItem> Items { get; }

    public bool Aborted { get; }

    public string? AbortCode { get; }

    public string? BackupZipPath { get; }

    public string? BackupSignature { get; }

    public int BackupEntryCount { get; }

    public long BackupTotalBytes { get; }

    public int DeletedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Deleted);
    public int SkippedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Skipped);
    public int FailedCount => Items.Count(static i => i.Status == OutcomeItemStatus.Failed);

    /// <summary>实际释放字节合计（仅 Deleted 项计入）。</summary>
    public long FreedBytes => Items.Sum(static i => i.FreedBytes);

    /// <summary>部分失败：未中止且存在跳过或失败项（TC-16 场景）。</summary>
    public bool PartialFailure => !Aborted && (SkippedCount + FailedCount) > 0;

    /// <summary>
    /// 脱敏结构化日志（每行 key=value）：1 行 run_summary + 1 行 backup + 每项 1 行 item。
    /// 与 UI 显示同源同数：逐项状态/错误码/字节一致；不含路径、正文、自由文本。
    /// </summary>
    public string BuildLog(DateTimeOffset? nowUtc = null)
    {
        var ts = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();

        sb.Append("ts=").Append(ts).Append(" event=run_summary")
            .Append(" aborted=").Append(Aborted ? "true" : "false");
        if (Aborted && AbortCode is { Length: > 0 })
        {
            sb.Append(" code=").Append(SafeCode(AbortCode));
        }

        sb.Append(" deleted=").Append(DeletedCount)
            .Append(" skipped=").Append(SkippedCount)
            .Append(" failed=").Append(FailedCount)
            .Append(" freed_bytes=").Append(FreedBytes)
            .Append(" items=").Append(Items.Count)
            .Append('\n');

        sb.Append("ts=").Append(ts).Append(" event=backup")
            .Append(" zip_name=").Append(SafeZipNameInput(BackupZipPath))
            .Append(" entries=").Append(BackupEntryCount)
            .Append(" bytes=").Append(BackupTotalBytes)
            .Append(" sig=").Append(BackupSignature is { Length: > 0 } sig && SafeSignature.IsMatch(sig) ? sig : "-")
            .Append('\n');

        foreach (var item in Items)
        {
            sb.Append("ts=").Append(ts).Append(" event=item")
                .Append(" category=").Append(item.Category.ToString())
                .Append(" status=").Append(item.Status.ToString().ToLowerInvariant())
                .Append(" code=").Append(item.ErrorCode is { Length: > 0 } code ? SafeCode(code) : "-")
                .Append(" bytes=").Append(item.FreedBytes)
                .Append(" session=").Append(item.OwnerSessionId is { Length: > 0 } id && SafeSessionId.IsMatch(id) ? id : "-")
                .Append('\n');
        }

        if (Input.SessionAudit is { } audit)
        {
            sb.Append("ts=").Append(ts).Append(" event=session_audit_summary")
                .Append(" sessions=").Append(Sessions.Count).Append(" physical_residue=").Append(PhysicalResidueSessionCount)
                .Append(" metadata_residue=").Append(MetadataResidueSessionCount).Append(" unknown=").Append(UnknownSessionCount)
                .Append(" issues=").Append(AuditIssues.Count).Append(" has_unknowns=").Append(HasAuditUnknowns ? "true" : "false")
                .Append(" observed_utc=").Append(audit.ObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            for (var index = 0; index < Sessions.Count; index++)
            {
                var session = Sessions[index];
                sb.Append("ts=").Append(ts).Append(" event=session_audit session_index=").Append(index + 1)
                    .Append(" state=").Append(session.State.ToString())
                    .Append(" residue=").Append(session.ResidueCategories.Count == 0 ? "-" : string.Join(',', session.ResidueCategories))
                    .Append(" present=").Append(session.PresentTargets).Append(" absent=").Append(session.AbsentTargets)
                    .Append(" target_unknown=").Append(session.UnknownTargets).Append(" observed_logs=").Append(session.ObservedLogCount)
                    .Append(" metadata_refs=").Append(session.WorkspaceReferenceCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                    .Append(" has_unknowns=").Append(session.HasUnknowns ? "true" : "false").Append('\n');
            }
            foreach (var issue in AuditIssues)
            {
                var index = Sessions.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.SessionId == issue.SessionId);
                sb.Append("ts=").Append(ts).Append(" event=session_audit_issue")
                    .Append(" code=").Append(AuditLogCodes.Contains(issue.Code) ? issue.Code : "OTHER_AUDIT_ISSUE")
                    .Append(" session_index=").Append(index.s is null ? "-" : (index.i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        if (WorkspaceSync is { } sync)
        {
            sb.Append("ts=").Append(ts).Append(" event=workspace_sync")
                .Append(" state=").Append(Enum.IsDefined(sync.State) ? sync.State : "Unknown")
                .Append(" proposed_refs=").Append(Math.Max(0, sync.ProposedReferenceCount))
                .Append(" removed_refs=").Append(sync.RemovedReferenceCount is >= 0 ? sync.RemovedReferenceCount.Value.ToString(CultureInfo.InvariantCulture) : "unknown")
                .Append(" backup_present=").Append(sync.BackupDirectory is null ? "false" : "true")
                .Append(" code=").Append(SyncLogCodes.Contains(sync.ErrorCode ?? "") ? sync.ErrorCode : sync.ErrorCode is null ? "-" : "OTHER_SYNC_ERROR")
                .Append(" observed_utc=").Append(sync.ObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> SyncLogCodes = new(StringComparer.Ordinal)
    {
        "SYNC_PRODUCTION_VERSION_NOT_ALLOWED", "SYNC_BACKUP_ROOT_REQUIRED", "SYNC_PHYSICAL_CONTEXT_INVALID",
        "SYNC_CANCELED", "SYNC_COORDINATION_FAILED", "SYNC_RETRY_CONTEXT_INVALID", "SYNC_RECOVERY_REQUIRED",
        "SYNC_PREPARE_FAILED", "SYNC_APPLY_FAILED", "SYNC_PROCESS_NOT_INACTIVE", "SYNC_BACKUP_INVALID",
        "SYNC_SOURCE_UNKNOWN_OR_CHANGED", "SYNC_SOURCE_IDENTITY_CHANGED", "SYNC_CANDIDATE_CHANGED",
        "SYNC_ARTIFACT_EXISTS_OR_UNSAFE", "SYNC_UNFINISHED_CANDIDATE", "SYNC_PHYSICAL_PROOF_INVALID",
        "SYNC_LOG_ABSENCE_UNKNOWN", "SYNC_NO_ELIGIBLE_REMOVALS", "SYNC_REFERENCE_SNAPSHOT_UNKNOWN",
        "SYNC_REPORT_PLAN_MISMATCH", "SYNC_RECOVERY_UNVERIFIABLE", "SYNC_RECOVERY_CANCELED",
        "SYNC_REPORT_STATE_INVALID",
    };

    private static OutcomeSessionRow ProjectSession(PostCleanupSessionResult source, bool globalUnknown)
    {
        var categories = new HashSet<OutcomeResidueCategory>();
        if (source.ObservedLogCount > 0) categories.Add(OutcomeResidueCategory.SessionLog);
        if (source.WorkspaceReferenceCount > 0) categories.Add(OutcomeResidueCategory.WorkspaceMetadata);
        foreach (var target in source.Targets.Where(x => x.Presence == TargetPresence.Present))
            categories.Add(target.Category switch
            {
                CleanupCategory.SessionLog => OutcomeResidueCategory.SessionLog,
                CleanupCategory.DedicatedCache => OutcomeResidueCategory.DedicatedCache,
                CleanupCategory.Attachment => OutcomeResidueCategory.Attachment,
                _ => OutcomeResidueCategory.OtherPhysical,
            });
        var invalid = !Enum.IsDefined(source.State) || source.ObservedLogCount < 0 || source.WorkspaceReferenceCount < 0
            || source.Targets.Any(x => !Enum.IsDefined(x.Presence));
        var state = invalid ? SessionCleanupState.Unknown : source.State;
        return new OutcomeSessionRow(source.SessionId, state, categories.OrderBy(x => x).ToArray(),
            source.Targets.Count(x => x.Presence == TargetPresence.Present), source.Targets.Count(x => x.Presence == TargetPresence.Absent),
            source.Targets.Count(x => x.Presence == TargetPresence.Unknown || !Enum.IsDefined(x.Presence)), Math.Max(0, source.ObservedLogCount),
            source.WorkspaceReferenceCount >= 0 ? source.WorkspaceReferenceCount : null,
            globalUnknown || source.HasUnknowns || state == SessionCleanupState.Unknown);
    }

    private static IEnumerable<SessionResidueIssue> RelationIssues(SessionResidueSnapshot? snapshot)
    {
        if (snapshot is null) yield break;
        foreach (var session in snapshot.Sessions)
            foreach (var item in session.Items.Where(x => x.Scope == SessionResidueScope.UnknownReference))
                yield return new SessionResidueIssue(item.Reason ?? "AUDIT_RELATION_UNKNOWN", item.EvidencePath, session.SessionId);
        foreach (var item in snapshot.UnattributedItems.Where(x => x.Scope == SessionResidueScope.UnknownReference))
            yield return new SessionResidueIssue(item.Reason ?? "AUDIT_RELATION_UNKNOWN", item.EvidencePath, null);
    }

    private static string SafeCode(string code) =>
        SafeErrorCode.IsMatch(code) ? code : "UNSAFE_CODE";

    /// <summary>备份位置脱敏：日志只记文件名（白名单内），目录部分（可能含用户名）不出现。</summary>
    private static string SafeZipNameInput(string? zipPath)
    {
        if (zipPath is not { Length: > 0 })
        {
            return "-";
        }

        var name = Path.GetFileName(zipPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SafeZipName.IsMatch(name) ? name : "-";
    }
}
