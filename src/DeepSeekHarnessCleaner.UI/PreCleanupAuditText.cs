using System.Text;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>Confirmation-only evidence text; contains no transcript/attachment display names and is not a log export.</summary>
public static class PreCleanupAuditText
{
    public static string Render(PreCleanupAudit audit)
    {
        var text = new StringBuilder();
        text.AppendLine("删除前只读审计：仅“待删除文件”页中的物理目标会被删除。");
        text.AppendLine("Workspace 引用、保留附件和全量缓存不会因本次审计自动加入删除范围。");
        text.AppendLine(audit.Snapshot.HasUnknowns ? "存在无法确认项，不能据此认定会话已彻底清理。" : "本次审计未发现未知项；仍须执行删除前复验及删除后审计。");
        text.AppendLine("审计时间：" + audit.ObservedUtc.ToString("O"));
        text.AppendLine();
        foreach (var session in audit.Snapshot.Sessions)
        {
            text.AppendLine("会话：" + session.SessionId);
            if (session.Items.Count == 0) text.AppendLine("  未发现已证实关联；请同时检查下方未知项。");
            foreach (var item in session.Items) Item(item);
        }
        text.AppendLine();
        text.AppendLine("未归属单会话的证据（不推测归属）：");
        foreach (var item in audit.Snapshot.UnattributedItems) Item(item);
        text.AppendLine();
        text.AppendLine("无法确认项 / 审计异常：");
        foreach (var issue in audit.Snapshot.Issues)
            text.AppendLine($"  {issue.Code} {issue.SessionId} {issue.EvidencePath}");
        text.AppendLine("Workspace 快照状态：" + audit.Snapshot.Workspace.Status);
        return text.ToString();

        void Item(SessionResidueItem item)
        {
            var scope = item.Scope switch { SessionResidueScope.TargetFile => "目标文件", SessionResidueScope.NotInScope => "非本次清理范围", _ => "未知引用" };
            var kind = item.Kind switch
            {
                SessionResidueKind.SessionLog => "日志代际 " + Path.GetFileName(item.EvidencePath),
                SessionResidueKind.DedicatedCache => "专属缓存",
                SessionResidueKind.WholeCache => "全量缓存",
                SessionResidueKind.Attachment => "附件",
                SessionResidueKind.WorkspaceReference => "Workspace 引用",
                _ => "未证实关联的计划目标",
            };
            text.AppendLine($"  [{scope}] {kind}");
            text.AppendLine($"    {item.EvidencePath}{(item.EvidencePointer is null ? "" : "#" + item.EvidencePointer)}");
            if (item.RelatedSessionIds.Count > 1) text.AppendLine("    共享引用会话：" + string.Join("、", item.RelatedSessionIds));
            if (item.Reason is not null) text.AppendLine("    " + item.Reason);
        }
    }
}
