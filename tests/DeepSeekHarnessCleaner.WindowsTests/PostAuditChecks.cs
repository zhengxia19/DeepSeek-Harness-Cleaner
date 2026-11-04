using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static int RunPostAuditChecks()
    {
        foreach (var corrupt in new[] { false, true })
        {
            var fixture = Path.GetFullPath(Path.Combine(".artifacts", "T10", "ui-" + Guid.NewGuid().ToString("N")));
            var home = Path.Combine(fixture, ".dsh");
            var sessions = Path.Combine(home, "sessions");
            var storage = Path.Combine(home, "storages");
            Directory.CreateDirectory(storage);
            Directory.CreateDirectory(Path.Combine(home, "attachments"));
            var selected = WriteSession(sessions, "post-selected", new string('a', 64));
            var keep = WriteSession(sessions, "post-keep", new string('b', 64));
            var workspace = Path.Combine(storage, "workspace.json");
            var json = JsonSerializer.Serialize(new
            {
                unit = new { name = "workspace", version = 2 },
                global = new { initialized = true, archivedSessionIds = new[] { "post-selected" }, pinnedSessionIds = Array.Empty<string>(), workspaceIds = new[] { "synthetic" } },
                tables = new { workspaces = new Dictionary<string, object> { ["synthetic"] = new { path = fixture, title = "synthetic", sessionIds = new[] { "post-selected", "post-keep" }, createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" } } },
            });
            File.WriteAllText(workspace, json);
            var sentinel = Path.Combine(fixture, "outside-sentinel.txt");
            File.WriteAllText(sentinel, "synthetic protected sentinel");
            var protectedHashes = new[] { keep, workspace, sentinel }.ToDictionary(x => x, Hash);
            // Inactive process injection applies only to this fresh fixture; default production uses the real probe.
            using var main = new MainForm(scanRootPath: sessions, logDirPath: Path.Combine(fixture, "tool-logs"),
                persistWindowState: false, actionsPresenter: new ActionsPresenter(() => []));
            try
            {
                main.Show();
                var grid = Field<DataGridView>(main, "_sessionGrid");
                Field<Button>(main, "_scanButton").PerformClick();
                Pump(() => grid.Rows.Count == 2 && !Field<bool>(main, "_scanning"));
                var row = grid.Rows.Cast<DataGridViewRow>().Single(x => x.Tag is SessionSummary { SessionId: "post-selected" });
                row.Cells["checked"].Value = true;
                Field<Button>(main, "_deleteButton").PerformClick();
                Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
                var dialog = Field<CleanupDialog>(main, "_lastCleanupDialog");
                Check("post audit case has valid pre-audit", dialog.Audit!.Matches(dialog.Plan));
                if (corrupt) File.WriteAllText(workspace, "{"); // Controlled fault after preflight, before delete.
                dialog.RequestConfirm();
                Pump(() => dialog.RunFinished && !Field<bool>(main, "_scanning") && Field<ResultsDialog?>(main, "_lastResultsDialog") is { Visible: true });
                Check("synthetic file executor reports success", dialog.LastResult!.AllSucceeded && !File.Exists(selected));
                var post = Field<PostCleanupAudit>(main, "_lastPostCleanupAudit");
                Check("post audit actually observes original target absent", post.Targets.Count == 1 && post.Targets[0].Presence == TargetPresence.Absent);
                var state = post.Sessions.Single();
                var status = Field<ToolStripStatusLabel>(main, "_statusLabel").Text ?? string.Empty;
                if (corrupt)
                {
                    Check("failed metadata recheck is unknown in real callback", state.State == SessionCleanupState.Unknown && state.WorkspaceReferenceCount is null);
                    Check("unknown recheck is visible on main", status.Contains("未知", StringComparison.Ordinal));
                    File.WriteAllText(workspace, json);
                }
                else
                {
                    Check("real callback separates physical success and metadata residue", state.State == SessionCleanupState.MetadataResidue && state.WorkspaceReferenceCount == 2);
                    Check("metadata residue is visible on main", status.Contains("元数据残留", StringComparison.Ordinal));
                }
                var reportDialog = Field<ResultsDialog>(main, "_lastResultsDialog");
                Check("result window keeps independent file and session rows", reportDialog.DisplayedItemCount == 1
                    && reportDialog.DisplayedSessionCount == 1 && reportDialog.SummaryText.Contains("删除 1", StringComparison.Ordinal));
                var reportGrid = Field<DataGridView>(reportDialog, "_sessionGrid");
                Check("session report displays correct residual/unknown state", (string?)reportGrid.Rows[0].Cells[1].Value == (corrupt ? "未知" : "元数据残留"));
                Check("session report does not coerce unknown metadata count to zero", (string?)reportGrid.Rows[0].Cells[7].Value == (corrupt ? "未知" : "2"));
                Check("session report retains unknown caveat", reportDialog.SessionSummaryText.Contains("含未知项 1", StringComparison.Ordinal)
                    && reportDialog.SessionSummaryText.Contains("不能认定彻底清理", StringComparison.Ordinal));
                Check("audit issues displayed separately", reportDialog.DisplayedAuditIssueCount > 0 && Field<TabControl>(reportDialog, "_resultTabs").TabPages.Count == 3);
                var log = File.ReadAllText(reportDialog.LogFilePath!);
                Check("metadata outcome is independently NotRequested in real callback", Field<WorkspaceSyncOutcome>(main, "_lastWorkspaceSyncOutcome").State == WorkspaceSyncState.NotRequested);
                Check("metadata opt-out is visible and logged", Field<TextBox>(reportDialog, "_backupLabel").Text.Contains("未请求，未写入元数据", StringComparison.Ordinal)
                    && log.Contains("event=workspace_sync state=NotRequested", StringComparison.Ordinal));
                Check("new log has independent session audit events", log.Contains("event=session_audit_summary", StringComparison.Ordinal)
                    && log.Contains("metadata_refs=" + (corrupt ? "unknown" : "2"), StringComparison.Ordinal));
                Check("audit log omits all evidence paths and JSON pointers", !log.Contains(home, StringComparison.OrdinalIgnoreCase)
                    && !log.Contains("workspace.json", StringComparison.Ordinal) && !log.Contains("/global/", StringComparison.Ordinal));
                var tabs = Field<TabControl>(reportDialog, "_resultTabs");
                tabs.SelectedIndex = 1;
                Application.DoEvents();
                SaveScreenshot(reportDialog, Path.Combine(fixture, "report-session.png"));
                tabs.SelectedIndex = 2;
                Application.DoEvents();
                SaveScreenshot(reportDialog, Path.Combine(fixture, "report-issues.png"));
                reportDialog.Close();
                dialog.RequestCancel();
                SaveScreenshot(main, Path.Combine(fixture, corrupt ? "post-unknown.png" : "post-metadata-residue.png"));
                foreach (var file in protectedHashes) Check("post audit protected hash " + Path.GetFileName(file.Key), Hash(file.Key) == file.Value);
                Check("post audit refresh lists only remaining session", grid.Rows.Count == 1 && grid.Rows[0].Tag is SessionSummary { SessionId: "post-keep" });
                Console.WriteLine("T10_FIXTURE " + fixture);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("T10_FIXTURE " + fixture); return 1; }
            finally { main.Close(); }
        }
        return RunWorkspaceOutcomeReportChecks();
    }

    private static int RunWorkspaceOutcomeReportChecks()
    {
        var fixture = Path.GetFullPath(Path.Combine(".artifacts", "T15", "report-ui-" + Guid.NewGuid().ToString("N")));
        var backup = Directory.CreateDirectory(Path.Combine(fixture, "metadata-backup")).FullName;
        foreach (var state in new[] { WorkspaceSyncState.Applied, WorkspaceSyncState.RecoveryRequired })
        {
            var sync = new WorkspaceSyncOutcome(Guid.NewGuid(), Guid.NewGuid(), state,
                state == WorkspaceSyncState.RecoveryRequired ? "SYNC_CANCELED" : null, 2,
                state == WorkspaceSyncState.Applied ? 2 : null, backup, DateTimeOffset.UtcNow);
            var report = new OutcomeReport(new OutcomeReportInput(false, null, null, null, 0, 0,
                [new("synthetic-deleted", CleanupCategory.SessionLog, null, OutcomeItemStatus.Deleted, null, 10),
                 new("synthetic-failed", CleanupCategory.SessionLog, null, OutcomeItemStatus.Failed, "FILE_CHANGED", 0)])
            { WorkspaceSync = sync });
            using var dialog = new ResultsDialog(report, Path.Combine(fixture, "logs"));
            try
            {
                dialog.Show();
                Application.DoEvents();
                var text = Field<TextBox>(dialog, "_backupLabel").Text;
                Check("metadata report preserves physical file counts " + state, dialog.SummaryText.Contains("删除 1", StringComparison.Ordinal) && dialog.DisplayedItemCount == 2);
                Check("metadata report shows separate state and mandatory backup " + state,
                    text.Contains(state == WorkspaceSyncState.Applied ? "已验证移除 2" : "移除数量未知", StringComparison.Ordinal)
                    && text.Contains(backup, StringComparison.Ordinal) && text.Contains("不构成原子事务", StringComparison.Ordinal));
                Check("metadata backup text remains scrollable " + state, Field<TextBox>(dialog, "_backupLabel").ScrollBars == ScrollBars.Vertical);
                var log = File.ReadAllText(dialog.LogFilePath!);
                Check("metadata outcome log excludes backup path " + state, !log.Contains(backup, StringComparison.OrdinalIgnoreCase)
                    && log.Contains("removed_refs=" + (state == WorkspaceSyncState.Applied ? "2" : "unknown"), StringComparison.Ordinal));
                SaveScreenshot(dialog, Path.Combine(fixture, "report-" + state + ".png"));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { dialog.Close(); }
        }
        Console.WriteLine("T15_REPORT_FIXTURE " + fixture);
        return 0;
    }
}
