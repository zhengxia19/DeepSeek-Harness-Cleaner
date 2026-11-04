using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static int RunAuditChecks()
    {
        var fixture = Path.GetFullPath(Path.Combine(".artifacts", "T09", "ui-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(fixture, ".dsh");
        var sessions = Path.Combine(home, "sessions");
        var storage = Path.Combine(home, "storages");
        var attachments = Path.Combine(home, "attachments");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(attachments);
        var content = new byte[] { 5, 8, 13 };
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var attachment = Path.Combine(attachments, "v1", "objects", sha[..2], sha);
        Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);
        File.WriteAllBytes(attachment, content);
        var selected = WriteSession(sessions, "audit-selected", sha);
        var keep = WriteSession(sessions, "audit-keep", sha);
        var cache = Path.Combine(storage, "session_projcache", "sessions", "audit-selected.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "{}");
        var workspace = Path.Combine(storage, "workspace.json");
        var json = JsonSerializer.Serialize(new
        {
            unit = new { name = "workspace", version = 2 },
            global = new { initialized = true, archivedSessionIds = new[] { "audit-selected" }, pinnedSessionIds = new[] { "audit-keep" }, workspaceIds = new[] { "synthetic" } },
            tables = new { workspaces = new Dictionary<string, object> { ["synthetic"] = new { path = fixture, title = "synthetic", sessionIds = new[] { "audit-selected", "audit-keep" }, createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" } } },
        });
        File.WriteAllText(workspace, json);
        var sentinel = Path.Combine(fixture, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "protected synthetic sentinel");
        var hashes = new[] { selected, keep, cache, attachment, workspace, sentinel }.ToDictionary(x => x, Hash);
        using var main = new MainForm(scanRootPath: sessions, persistWindowState: false);
        try
        {
            main.Show();
            var grid = Field<DataGridView>(main, "_sessionGrid");
            Field<Button>(main, "_scanButton").PerformClick();
            Pump(() => grid.Rows.Count == 2 && !Field<bool>(main, "_scanning"));
            var row = grid.Rows.Cast<DataGridViewRow>().Single(x => x.Tag is SessionSummary { SessionId: "audit-selected" });
            row.Cells["checked"].Value = true;
            Field<Button>(main, "_deleteButton").PerformClick();
            Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
            var dialog = Field<CleanupDialog>(main, "_lastCleanupDialog");
            var audit = dialog.Audit!;
            Check("main confirmation carries plan-bound audit", audit is not null && audit.Matches(dialog.Plan));
            Check("main physical target remains one log only", dialog.Plan.Targets.Count == 1 && dialog.Plan.Targets[0].AbsolutePath == selected);
            Check("audit snapshot has verified Workspace state", audit!.Snapshot.Workspace.Status == WorkspaceReferenceStatus.Known);
            var items = audit.Snapshot.Sessions.Single().Items;
            Check("unselected dedicated cache is shown out of scope", items.Any(x => x.Kind == SessionResidueKind.DedicatedCache && x.Scope == SessionResidueScope.NotInScope));
            Check("shared attachment is shown retained with both owners", items.Any(x => x.Kind == SessionResidueKind.Attachment && x.Scope == SessionResidueScope.NotInScope && x.RelatedSessionIds.Count == 2));
            Check("archive and member refs shown without physical expansion", items.Count(x => x.Kind == SessionResidueKind.WorkspaceReference && x.Scope == SessionResidueScope.NotInScope) == 2);
            Check("default confirmation warns about audit and unknowns", Field<TextBox>(dialog, "_warningLabel").Text.Contains("只读审计", StringComparison.Ordinal)
                && Field<TextBox>(dialog, "_warningLabel").Text.Contains("未知", StringComparison.Ordinal));
            var tabs = Field<TabControl>(dialog, "_evidenceTabs");
            var auditText = Field<TextBox>(dialog, "_auditText");
            Check("files and read-only audit are separate pages", tabs.TabPages.Count == 2 && auditText.ReadOnly && auditText.ScrollBars == ScrollBars.Both);
            Check("audit lists generation cache refs and unknown items", auditText.Text.Contains("日志代际 session.v4.jsonl.zstd", StringComparison.Ordinal)
                && auditText.Text.Contains("专属缓存", StringComparison.Ordinal) && auditText.Text.Contains("Workspace 引用", StringComparison.Ordinal)
                && auditText.Text.Contains("ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN", StringComparison.Ordinal));
            SaveScreenshot(dialog, Path.Combine(fixture, "confirm-files.png"));
            tabs.SelectedIndex = 1;
            Application.DoEvents();
            SaveScreenshot(dialog, Path.Combine(fixture, "confirm-audit.png"));
            var originalPlan = dialog.Plan;
            dialog.RequestCancel();
            foreach (var file in hashes) Check("audit/cancel preserve hash " + Path.GetFileName(file.Key), Hash(file.Key) == file.Value);
            using (var mismatch = new CleanupDialog(new CleanupPlanResult(null, originalPlan with { PlanId = Guid.NewGuid() }, []),
                sessions, home, [], new ActionsPresenter(() => []), Path.Combine(fixture, "not-created.zip"), preCleanupAudit: audit))
            {
                mismatch.Show(main);
                Check("mismatched audit disables confirmation", !mismatch.ConfirmButtonEnabled);
                mismatch.RequestConfirm();
                Check("mismatched audit cannot execute", mismatch.LastResult is null && Hash(selected) == hashes[selected]);
                mismatch.RequestCancel();
            }
            File.WriteAllText(workspace, "{");
            Field<Button>(main, "_deleteButton").PerformClick();
            Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
            var unknownDialog = Field<CleanupDialog>(main, "_lastCleanupDialog");
            Check("corrupt metadata appears as unknown rather than no refs", unknownDialog.Audit!.Snapshot.Workspace.Status == WorkspaceReferenceStatus.Unknown
                && Field<TextBox>(unknownDialog, "_auditText").Text.Contains("WORKSPACE_JSON_INVALID", StringComparison.Ordinal));
            Check("unknown metadata does not become deletion target", unknownDialog.Plan.Targets.Count == 1 && unknownDialog.Plan.Targets[0].AbsolutePath == selected);
            unknownDialog.RequestCancel();
            File.WriteAllText(workspace, json);
            foreach (var file in hashes) Check("final protected hash " + Path.GetFileName(file.Key), Hash(file.Key) == file.Value);
            Console.WriteLine("T09_FIXTURE " + fixture);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("T09_FIXTURE " + fixture); return 1; }
        finally { main.Close(); }
    }
}
