using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.UI;

public static class PostCleanupAuditText
{
    public static string Summary(PostCleanupAudit? audit)
    {
        if (audit is null || audit.Sessions.Count == 0) return "会话复查：未知（没有可用审计）";
        var parts = new List<string>();
        var physical = audit.Sessions.Count(x => x.State == SessionCleanupState.PhysicalResidue);
        var metadata = audit.Sessions.Count(x => x.WorkspaceReferenceCount > 0);
        if (physical > 0) parts.Add($"文件残留 {physical} 个会话");
        if (metadata > 0) parts.Add($"元数据残留 {metadata} 个会话（Workspace 引用未清理）");
        if (audit.HasUnknowns) parts.Add(parts.Count > 0 ? "另有未知项，不能认定彻底清理" : "未知（复查不完整），不能认定彻底清理");
        if (parts.Count == 0) parts.Add("本次范围内未观察到残留；不代表全部历史数据已清理");
        return "会话复查：" + string.Join("；", parts);
    }
}
