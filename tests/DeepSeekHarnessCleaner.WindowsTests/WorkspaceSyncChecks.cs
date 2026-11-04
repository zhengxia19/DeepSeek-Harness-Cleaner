using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static int RunWorkspaceSyncChecks()
    {
        foreach (var mode in new[] { "production", "unchecked", "checked", "cancel", "interrupted", "process-change", "source-change", "invalid-backup" })
        {
            var root = Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures", "fx-" + Guid.NewGuid().ToString("N"));
            var home = Path.Combine(root, ".dsh");
            var sessions = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
            var storages = Directory.CreateDirectory(Path.Combine(home, "storages")).FullName;
            Directory.CreateDirectory(Path.Combine(home, "attachments"));
            var backupRoot = Directory.CreateDirectory(Path.Combine(root, "metadata-backups")).FullName;
            var output = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(".artifacts", "T16", "ui-" + mode + "-" + Guid.NewGuid().ToString("N")))).FullName;
            string Log(string id)
            {
                var directory = Directory.CreateDirectory(Path.Combine(sessions, "project", id)).FullName;
                var path = Path.Combine(directory, "session.v4.jsonl");
                File.WriteAllText(path, JsonSerializer.Serialize(new { type = "session", id, version = 4, createdAt = 1767225600000L, cwd = Path.Combine(root, "project") }) + "\n");
                return path;
            }
            var selected = Log("sync-selected");
            var keep = Log("sync-keep");
            var workspace = Path.Combine(storages, "workspace.json");
            File.WriteAllText(workspace, JsonSerializer.Serialize(new
            {
                unit = new { name = "workspace", version = 2 },
                global = new { initialized = true, workspaceIds = new[] { "work" }, defaultWorkspaceId = "work", archivedSessionIds = new[] { "sync-selected" }, pinnedSessionIds = new[] { "sync-keep" } },
                tables = new { workspaces = new Dictionary<string, object> { ["work"] = new { path = root, title = "Synthetic", sessionIds = new[] { "sync-selected", "sync-keep" }, createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" } } },
            }));
            var original = File.ReadAllBytes(workspace);
            var sentinel = Path.Combine(root, "outside-sentinel");
            File.WriteAllText(sentinel, "protected synthetic sentinel");
            var hashes = new[] { keep, sentinel }.ToDictionary(x => x, Hash);
            var active = false;
            var inject = mode == "interrupted";
            var probe = new HarnessProcessProbe(() => active ? [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)] : []);
            var coordinator = mode == "production" ? new WorkspaceSyncCoordinator(probe) : new WorkspaceSyncCoordinator(home, probe, at =>
            {
                if (inject && at == WorkspaceWritePoint.OriginalRetained) { inject = false; throw new OperationCanceledException(); }
            });
            using var main = new MainForm(scanRootPath: sessions, logDirPath: Path.Combine(output, "logs"), persistWindowState: false,
                actionsPresenter: new ActionsPresenter(() => []), workspaceCoordinator: coordinator,
                workspaceRecovery: new WorkspaceRecoveryService(home, probe), workspaceBackupRoot: mode == "invalid-backup" ? storages : backupRoot);
            try
            {
                main.Show();
                Field<Button>(main, "_scanButton").PerformClick();
                var grid = Field<DataGridView>(main, "_sessionGrid");
                Pump(() => grid.Rows.Count == 2);
                var row = grid.Rows.Cast<DataGridViewRow>().Single(x => x.Tag is SessionSummary { SessionId: "sync-selected" });
                grid.ClearSelection();
                grid.CurrentCell = row.Cells["id"];
                row.Selected = true;
                Field<Button>(main, "_deleteButton").PerformClick();
                Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
                var dialog = Field<CleanupDialog>(main, "_lastCleanupDialog");
                var sync = Field<CheckBox>(dialog, "_workspaceSyncRequested");
                var info = Field<TextBox>(dialog, "_workspaceSyncInfo");
                Check("sync defaults off " + mode, !sync.Checked && !dialog.WorkspaceSyncRequested);
                Check("potential reference count and mandatory root visible " + mode, info.Text.Contains("2 处", StringComparison.Ordinal) && info.Text.Contains(mode == "invalid-backup" ? storages : backupRoot, StringComparison.Ordinal));
                Check("sync availability bound to actual gate " + mode, sync.Enabled == (mode is not ("production" or "invalid-backup")));
                Check("metadata option does not require session ZIP " + mode, !Field<CheckBox>(dialog, "_backupRequested").Checked && dialog.ConfirmButtonEnabled);
                SaveScreenshot(dialog, Path.Combine(output, "confirmation.png"));
                var originalSize = dialog.Size;
                dialog.Size = dialog.MinimumSize;
                Application.DoEvents();
                var optionControls = new Control[] { sync, info, Field<Button>(dialog, "_workspaceFolderButton"), Field<TabControl>(dialog, "_evidenceTabs"), Field<Button>(dialog, "_confirmButton") };
                var optionRects = optionControls.Select(c => dialog.RectangleToClient(c.RectangleToScreen(c.ClientRectangle))).ToArray();
                Check("sync confirmation minimum size has no overlap " + mode, optionRects.All(dialog.ClientRectangle.Contains)
                    && !optionRects.Where((rect, i) => optionRects.Skip(i + 1).Any(rect.IntersectsWith)).Any());
                SaveScreenshot(dialog, Path.Combine(output, "confirmation-minimum.png"));
                dialog.Size = originalSize;
                if (mode == "invalid-backup") { Check("unsafe backup root has visible reason", info.Text.Contains("禁用原因", StringComparison.Ordinal)); dialog.RequestCancel(); continue; }
                if (mode != "unchecked" && mode != "production") sync.Checked = true;
                if (mode == "production")
                {
                    sync.Checked = true; // Even programmatic checkbox spoofing cannot bypass the service-backed recheck.
                    dialog.RequestConfirm();
                    Check("disabled production checkbox cannot be spoofed into execution", !dialog.RunFinished && File.Exists(selected) && !sync.Checked);
                }
                if (sync.Checked) SaveScreenshot(dialog, Path.Combine(output, "confirmation-selected.png"));
                if (mode == "cancel")
                {
                    dialog.RequestCancel();
                    Check("cancel with sync selected changes nothing", File.Exists(selected) && File.ReadAllBytes(workspace).SequenceEqual(original) && Directory.GetFileSystemEntries(backupRoot).Length == 0);
                    Field<Button>(main, "_deleteButton").PerformClick();
                    Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
                    var fresh = Field<CleanupDialog>(main, "_lastCleanupDialog");
                    Check("sync consent not persisted into next confirmation", !Field<CheckBox>(fresh, "_workspaceSyncRequested").Checked);
                    fresh.RequestCancel();
                    continue;
                }
                if (mode == "process-change") active = true;
                if (mode == "source-change") File.AppendAllText(workspace, " ");
                dialog.RequestConfirm();
                if (mode is "process-change" or "source-change")
                {
                    Application.DoEvents();
                    Check("stale consent stops before physical deletion " + mode, !dialog.RunFinished && File.Exists(selected) && !sync.Checked && !sync.Enabled);
                    Check("stale consent explains re-confirmation " + mode, Field<Label>(dialog, "_resultLabel").Text.Contains("未开始删除", StringComparison.Ordinal));
                    dialog.RequestCancel();
                    continue;
                }
                Pump(() => dialog.RunFinished && !Field<bool>(main, "_scanning") && Field<ResultsDialog?>(main, "_lastResultsDialog") is { Visible: true });
                Check("physical deletion completes once " + mode, !File.Exists(selected) && dialog.LastResult!.DeletedCount == 1);
                var result = Field<WorkspaceSyncOutcome>(main, "_lastWorkspaceSyncOutcome");
                if (mode is "unchecked" or "production")
                {
                    Check("unchecked path never writes metadata " + mode, result.State == WorkspaceSyncState.NotRequested && File.ReadAllBytes(workspace).SequenceEqual(original) && Directory.GetFileSystemEntries(backupRoot).Length == 0);
                }
                else
                {
                    var attempt = Field<WorkspaceSyncAttempt>(main, "_lastWorkspaceSyncAttempt");
                    Check("mandatory backup exists without session ZIP " + mode, attempt.Backup is not null && File.ReadAllBytes(attempt.Backup.RawPath).SequenceEqual(original)
                        && dialog.Backup is null && Directory.GetFiles(backupRoot, "*.zip", SearchOption.AllDirectories).Length == 0);
                    if (mode == "interrupted")
                    {
                        Check("real callback reports interrupted metadata separately", result.State == WorkspaceSyncState.RecoveryRequired && !File.Exists(workspace));
                        var report = Field<ResultsDialog>(main, "_lastResultsDialog");
                        var oldLogPath = report.LogFilePath!;
                        var oldLog = File.ReadAllBytes(oldLogPath);
                        dialog.RequestCancel();
                        ((Button)report.Controls.Find("workspaceReviewButton", true).Single()).PerformClick();
                        Pump(() => Field<WorkspaceSyncReviewDialog?>(main, "_lastWorkspaceReview") is { Visible: true });
                        var review = Field<WorkspaceSyncReviewDialog>(main, "_lastWorkspaceReview");
                        typeof(MainForm).GetMethod("OnChooseRoots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [main, EventArgs.Empty]);
                        Check("open review blocks silent root switching", Field<string>(main, "_effectiveScanRoot") == sessions
                            && (Field<ToolStripStatusLabel>(main, "_statusLabel").Text ?? "").Contains("同步复查", StringComparison.Ordinal));
                        Check("retry and restore initially disabled", !Field<Button>(review, "_retry").Enabled && !Field<Button>(review, "_resolve").Enabled);
                        Field<Button>(review, "_inspect").PerformClick();
                        Pump(() => !Field<bool>(review, "_busy") && Field<WorkspaceRecoveryResult?>(review, "_inspection") is not null);
                        Check("inspection proves retained original before recovery", Field<WorkspaceRecoveryResult>(review, "_inspection").State == WorkspaceRecoveryState.OriginalRetained && !Field<Button>(review, "_resolve").Enabled);
                        Field<CheckBox>(review, "_approveRecovery").Checked = true;
                        Field<Button>(review, "_resolve").PerformClick();
                        Pump(() => !Field<bool>(review, "_busy"));
                        Check("explicit recovery restores only metadata", File.ReadAllBytes(workspace).SequenceEqual(original) && !File.Exists(selected) && Field<Button>(review, "_retry").Enabled);
                        SaveScreenshot(review, Path.Combine(output, "recovered.png"));
                        Field<Button>(review, "_retry").PerformClick();
                        Pump(() => !Field<bool>(review, "_busy") && Field<WorkspaceSyncOutcome>(main, "_lastWorkspaceSyncOutcome").State == WorkspaceSyncState.Applied);
                        Check("retry report preserves physical results and old log", Field<ResultsDialog>(main, "_lastResultsDialog").SummaryText.Contains("删除 1", StringComparison.Ordinal)
                            && oldLog.SequenceEqual(File.ReadAllBytes(oldLogPath)) && Field<ResultsDialog>(main, "_lastResultsDialog").LogFilePath != oldLogPath);
                        review.Close();
                    }
                    Check("checked callback removes exactly selected refs " + mode, Field<WorkspaceSyncOutcome>(main, "_lastWorkspaceSyncOutcome").State == WorkspaceSyncState.Applied
                        && !new WorkspaceReferenceReader().Read(home).References.Any(x => x.SessionId == "sync-selected")
                        && new WorkspaceReferenceReader().Read(home).References.Count(x => x.SessionId == "sync-keep") == 2);
                }
                foreach (var entry in hashes) Check("protected hash unchanged " + mode + " " + Path.GetFileName(entry.Key), Hash(entry.Key) == entry.Value);
                SaveScreenshot(Field<ResultsDialog>(main, "_lastResultsDialog"), Path.Combine(output, "result.png"));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("T16_FIXTURE " + root + " " + output); return 1; }
            finally { main.Close(); Console.WriteLine("T16_FIXTURE " + root + " " + output); }
        }
        return 0;
    }
}
