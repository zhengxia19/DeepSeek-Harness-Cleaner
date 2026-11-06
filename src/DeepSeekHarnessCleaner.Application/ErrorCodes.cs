namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 审计日志错误码的单一字面量定义点（B08），按族分组；字符串值与历史完全一致。
/// OutcomeReport.AuditLogCodes 白名单由本类常量组装；Infrastructure 侧同名公开常量以别名指向本类
/// （依赖方向 Infrastructure→Application）。仅覆盖进入审计日志白名单的错误码。
/// </summary>
internal static class ErrorCodes
{
    // —— 清理后审计族（PostCleanupAuditor / MainForm / OutcomeReport）——
    internal const string PostScanFailed = "POST_SCAN_FAILED";
    internal const string PostUiScanFailed = "POST_UI_SCAN_FAILED";
    internal const string PostPreAuditMissing = "POST_PRE_AUDIT_MISSING";
    internal const string PostAuditPlanMismatch = "POST_AUDIT_PLAN_MISMATCH";
    internal const string PostAuditRootInvalid = "POST_AUDIT_ROOT_INVALID";
    internal const string PostAuditRootMismatch = "POST_AUDIT_ROOT_MISMATCH";
    internal const string PostAuditRootUnavailable = "POST_AUDIT_ROOT_UNAVAILABLE";
    internal const string PostAuditReadFailed = "POST_AUDIT_READ_FAILED";
    internal const string PostTargetAssociationUnknown = "POST_TARGET_ASSOCIATION_UNKNOWN";
    internal const string PostTargetUnknown = "POST_TARGET_UNKNOWN";
    internal const string PostTargetChangedType = "POST_TARGET_CHANGED_TYPE";
    internal const string PostTargetUnreadable = "POST_TARGET_UNREADABLE";
    internal const string ReportPostAuditPlanMismatch = "REPORT_POST_AUDIT_PLAN_MISMATCH";

    // —— 工作区元数据读取族（WorkspaceReferenceReader / WorkspaceJson）——
    internal const string WorkspaceMissing = "WORKSPACE_MISSING";
    internal const string WorkspaceReadFailed = "WORKSPACE_READ_FAILED";
    internal const string WorkspaceJsonInvalid = "WORKSPACE_JSON_INVALID";
    internal const string WorkspaceVersionUnsupported = "WORKSPACE_VERSION_UNSUPPORTED";
    internal const string WorkspacePerRecordUnsupported = "WORKSPACE_PER_RECORD_UNSUPPORTED";
    internal const string WorkspaceLayoutAmbiguous = "WORKSPACE_LAYOUT_AMBIGUOUS";
    internal const string WorkspaceDuplicateField = "WORKSPACE_DUPLICATE_FIELD";
    internal const string WorkspaceNotInitialized = "WORKSPACE_NOT_INITIALIZED";
    internal const string WorkspaceTransactionUnknown = "WORKSPACE_TRANSACTION_UNKNOWN";
    internal const string WorkspaceIdsInvalid = "WORKSPACE_IDS_INVALID";
    internal const string WorkspaceRegistryMismatch = "WORKSPACE_REGISTRY_MISMATCH";
    internal const string WorkspaceMemberAmbiguous = "WORKSPACE_MEMBER_AMBIGUOUS";
    internal const string WorkspaceArchivePinConflict = "WORKSPACE_ARCHIVE_PIN_CONFLICT";
    internal const string WorkspaceRecordInvalid = "WORKSPACE_RECORD_INVALID";
    internal const string WorkspaceTooLarge = "WORKSPACE_TOO_LARGE";

    // —— 附件引用族（AttachmentInventoryService）——
    internal const string AttachmentReferenceCoverageUnknown = "ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN";
    internal const string AttachmentSessionCoverageUnknown = "ATTACHMENT_SESSION_COVERAGE_UNKNOWN";
    internal const string ReferenceUnreadable = "REFERENCE_UNREADABLE";
    internal const string ReferenceDecodeIncomplete = "REFERENCE_DECODE_INCOMPLETE";
    internal const string ReferenceReadLimit = "REFERENCE_READ_LIMIT";
    internal const string AttachmentChanged = "ATTACHMENT_CHANGED";
    internal const string AttachmentRootMissing = "ATTACHMENT_ROOT_MISSING";
    internal const string AttachmentFileMissing = "ATTACHMENT_FILE_MISSING";
    internal const string AttachmentRelationUnknown = "ATTACHMENT_RELATION_UNKNOWN";
    internal const string AuditRelationUnknown = "AUDIT_RELATION_UNKNOWN";

    // —— 会话/日志身份族（SessionResidueInventory）——
    internal const string SessionIdentityUnknown = "SESSION_IDENTITY_UNKNOWN";
    internal const string SessionIdentityDuplicate = "SESSION_IDENTITY_DUPLICATE";
    internal const string LogPathRejected = "LOG_PATH_REJECTED";
    internal const string LogUnreadable = "LOG_UNREADABLE";

    // —— 路径/根守卫族（PathGuard）——
    internal const string PathReparse = "PATH_REPARSE";
    internal const string PathEscape = "PATH_ESCAPE";
    internal const string RootInvalid = "ROOT_INVALID";
    internal const string RootUnreadable = "ROOT_UNREADABLE";
    internal const string FileAccessDenied = "FILE_ACCESS_DENIED";

    // —— 会话日志/缓存枚举族（SessionLogDiscovery / SessionCacheIndex / CacheCleanupSelection）——
    internal const string CacheStructureMismatch = "CACHE_STRUCTURE_MISMATCH";
    internal const string CacheNameCollision = "CACHE_NAME_COLLISION";
    internal const string CacheIdNotInInventory = "CACHE_ID_NOT_IN_INVENTORY";
    internal const string CacheInventoryMissing = "CACHE_INVENTORY_MISSING";
    internal const string NotWhitelisted = "NOT_WHITELISTED";
    internal const string UnexpectedLocation = "UNEXPECTED_LOCATION";
    internal const string Missing = "MISSING";
}
