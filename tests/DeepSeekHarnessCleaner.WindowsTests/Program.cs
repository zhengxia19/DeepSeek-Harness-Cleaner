using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Infrastructure.Compression;
using DeepSeekHarnessCleaner.UI;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        Control.CheckForIllegalCrossThreadCalls = true;
        var result = 1;
        using var dispatcher = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        dispatcher.Shown += (_, _) => { result = RunChecks(); dispatcher.Close(); };
        Application.Run(dispatcher);
        return result;
    }

    private static int RunChecks()
    {
        if (!File.Exists("global.json")) throw new InvalidOperationException("Run from repository root.");
        var fixture = Path.GetFullPath(Path.Combine(".artifacts", "functional-ui-" + Guid.NewGuid().ToString("N")));
        // The only test deletion targets are generated below this fresh repository-local fixture.
        var dataHome = Path.Combine(fixture, ".dsh");
        var sessionRoot = Path.Combine(dataHome, "sessions");
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(Path.Combine(dataHome, "storages"));
        var attachmentRoot = Path.Combine(dataHome, "attachments");
        Directory.CreateDirectory(attachmentRoot);
        byte[] content = [10, 20, 30];
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var attachment = Path.Combine(attachmentRoot, "v1", "objects", digest[..2], digest);
        Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);
        File.WriteAllBytes(attachment, content);
        var keepAttachment = Path.Combine(attachmentRoot, "keep.bin");
        File.WriteAllText(keepAttachment, "unselected attachment");
        var outside = Path.Combine(fixture, "outside-sentinel.bin");
        File.WriteAllText(outside, "outside protected sentinel");
        var a = WriteSession(sessionRoot, "ui-a", digest);
        var b = WriteSession(sessionRoot, "ui-b", digest);
        var workspace = Path.Combine(dataHome, "storages", "workspace.json");
        File.WriteAllText(workspace, """{"unit":{"name":"workspace","version":2},"global":{"initialized":true,"archivedSessionIds":["ui-a"]},"tables":{"workspaces":[]}}""");
        var keepHashes = new[] { keepAttachment, outside, b, workspace }.ToDictionary(x => x, Hash);
        using var main = new MainForm(scanRootPath: sessionRoot, logDirPath: Path.Combine(fixture, "logs"));
        try
        {
            main.Show();
            var grid = Field<DataGridView>(main, "_sessionGrid");
            Field<Button>(main, "_scanButton").PerformClick();
            Pump(() => grid.Rows.Count == 2);
            Check("native sessions listed with ID/title/date", grid.Rows.Cast<DataGridViewRow>().All(r => r.Tag is SessionSummary { SessionId: not null, CreatedAt: not null, Title: not null, Status: ParseStatus.Valid }));
            Check("normal parse status is visibly populated", grid.Rows.Cast<DataGridViewRow>().All(r => (string?)r.Cells["status"].Value == "正常"));
            Check("archive membership shown independently", grid.Rows.Cast<DataGridViewRow>().All(r => (string?)r.Cells["archive"].Value == (r.Tag is SessionSummary { SessionId: "ui-a" } ? "已归档" : "未归档")));
            Check("parse status column has explicit meaning", grid.Columns["status"]!.HeaderText == "解析状态");
            var row = grid.Rows.Cast<DataGridViewRow>().Single(x => x.Tag is SessionSummary { SessionId: "ui-a" });
            grid.CurrentCell = row.Cells["id"];
            row.Selected = true;
            var preview = Field<RichTextBox>(main, "_preview");
            Pump(() => preview.Text.Contains("synthetic tool result", StringComparison.Ordinal));
            Check("tool result shown without false unknown/corrupt markers", !preview.Text.Contains("不完整事件", StringComparison.Ordinal) && !preview.Text.Contains("未知事件类型", StringComparison.Ordinal));
            Field<Button>(main, "_deleteButton").PerformClick();
            Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
            var sessionDialog = Field<CleanupDialog>(main, "_lastCleanupDialog");
            CheckCentered("session delete centered on main", sessionDialog, main);
            Check("main delete opens a nonempty whole-session plan", sessionDialog.Plan.Targets.Count == 1);
            sessionDialog.RequestCancel();
            Check("cancel preserves selected session", File.Exists(a));

            var menu = Field<MenuStrip>(main, "_menu");
            menu.Items.Cast<ToolStripMenuItem>().Single(x => x.Text!.StartsWith("附件", StringComparison.Ordinal)).PerformClick();
            Pump(() => Field<AttachmentsForm?>(main, "_attachmentsWindow") is { Visible: true });
            var integrated = Field<AttachmentsForm>(main, "_attachmentsWindow");
            CheckCentered("attachment page centered on main", integrated, main);
            Pump(() => Field<ListView>(integrated, "_files").Items.Count == 2);
            Check("attachment page is reachable from main menu", true);
            integrated.Close();

            var actions = new ActionsPresenter(() => []); // Only fresh synthetic fixture uses injected Inactive.
            var sessions = new InventoryService(new UiScanSource()).Scan(sessionRoot).Sessions;
            using var attachments = new AttachmentsForm(dataHome, sessions, actions);
            attachments.Show(main);
            var files = Field<ListView>(attachments, "_files");
            Pump(() => files.Items.Count == 2);
            var item = files.Items.Cast<ListViewItem>().Single(x => x.Tag is AttachmentInventoryItem i && i.Candidate.FullPath == attachment);
            Check("shared attachment shows both referencing sessions", item.Tag is AttachmentInventoryItem i && i.ReferencingSessionIds.Count == 2);
            Check("unknown is not called an orphan", files.Items.Cast<ListViewItem>().Any(x => x.SubItems[4].Text.Contains("Unknown", StringComparison.Ordinal)));
            Check("attachment has readable name type and related title", item.SubItems[0].Text == "synthetic-photo.png"
                && item.SubItems[1].Text == "图片（image/png）" && item.SubItems[2].Text.Contains("Synthetic UI title", StringComparison.Ordinal));
            item.Checked = true;
            Field<Button>(attachments, "_delete").PerformClick();
            var cleanup = Field<CleanupDialog>(attachments, "_cleanup");
            CheckCentered("attachment delete centered on attachment parent", cleanup, attachments);
            Check("only explicit attachment selected", cleanup.Plan.Targets.Count == 1 && cleanup.Plan.Targets[0].AbsolutePath == attachment);
            Check("attachment deletion available without backup", cleanup.ConfirmButtonEnabled);
            Field<CheckBox>(cleanup, "_backupRequested").Checked = true;
            Check("selected backup requires verified ZIP", !cleanup.ConfirmButtonEnabled);
            cleanup.RequestConfirm();
            Check("selected but unfinished backup cannot delete", File.Exists(attachment));
            // Point generated backup output into the same isolated fixture.
            SetField(cleanup, "_zipPath", Path.Combine(fixture, "attachment-backup.zip"));
            cleanup.RequestBackup();
            Pump(() => cleanup.BackupReady);
            Check("attachment ZIP verified and confirmation enabled", cleanup.ConfirmButtonEnabled);
            cleanup.RequestConfirm();
            Pump(() => cleanup.RunFinished);
            Check("attachment deleted after backup and confirmation", cleanup.LastResult is { AllSucceeded: true } && !File.Exists(attachment));
            Check("session files preserved during attachment deletion", File.Exists(a) && File.Exists(b));
            cleanup.Close();
            attachments.Close();

            sessions = new InventoryService(new UiScanSource()).Scan(sessionRoot).Sessions;
            var chosen = sessions.Single(x => x.SessionId == "ui-a");
            var plan = actions.BuildSessionPlan(sessionRoot, [chosen]);
            using var deleteSession = new CleanupDialog(plan, sessionRoot, dataHome, [chosen], actions, Path.Combine(fixture, "session-backup.zip"));
            deleteSession.Show(main);
            Check("session deletion available without backup", deleteSession.ConfirmButtonEnabled && !deleteSession.BackupReady);
            deleteSession.RequestConfirm();
            Pump(() => deleteSession.RunFinished);
            Check("native session deleted without backup", deleteSession.LastResult is { AllSucceeded: true } && !File.Exists(a)
                && !File.Exists(Path.Combine(fixture, "session-backup.zip")) && deleteSession.Backup is null);
            deleteSession.Close();

            var failureTarget = WriteSession(sessionRoot, "ui-failure", digest);
            chosen = new InventoryService(new UiScanSource()).Scan(sessionRoot).Sessions.Single(s => s.SessionId == "ui-failure");
            var collisionZip = Path.Combine(fixture, "exists.zip");
            File.WriteAllText(collisionZip, "protected existing output");
            var collisionHash = Hash(collisionZip);
            using var failedBackup = new CleanupDialog(actions.BuildSessionPlan(sessionRoot, [chosen]), sessionRoot, dataHome, [chosen], actions, collisionZip);
            failedBackup.Show(main);
            failedBackup.RequestBackup();
            Pump(() => !Field<bool>(failedBackup, "_backingUp"));
            Check("failed backup keeps confirmation disabled", !failedBackup.BackupReady && !failedBackup.ConfirmButtonEnabled && File.Exists(failureTarget));
            Field<CheckBox>(failedBackup, "_backupRequested").Checked = false;
            Check("explicit opt out permits direct deletion after failed backup", failedBackup.ConfirmButtonEnabled);
            failedBackup.RequestConfirm();
            Pump(() => failedBackup.RunFinished);
            Check("opt out deletes exact session without overwriting existing ZIP", failedBackup.LastResult is { AllSucceeded: true }
                && !File.Exists(failureTarget) && Hash(collisionZip) == collisionHash && failedBackup.Backup is null);
            failedBackup.Close();

            var directAttachment = Path.Combine(attachmentRoot, "direct-delete.txt");
            File.WriteAllText(directAttachment, "synthetic direct deletion");
            using var directPage = new AttachmentsForm(dataHome, sessions, actions);
            directPage.Show(main);
            var directFiles = Field<ListView>(directPage, "_files");
            Pump(() => directFiles.Items.Count == 2);
            directFiles.Items.Cast<ListViewItem>().Single(x => x.Tag is AttachmentInventoryItem i && i.Candidate.FullPath == directAttachment).Checked = true;
            Field<Button>(directPage, "_delete").PerformClick();
            var directDelete = Field<CleanupDialog>(directPage, "_cleanup");
            directDelete.RequestConfirm();
            Pump(() => directDelete.RunFinished);
            Check("attachment deleted through UI without backup", directDelete.LastResult is { AllSucceeded: true }
                && !File.Exists(directAttachment) && directDelete.Backup is null && !File.Exists(directDelete.ZipPath));
            directDelete.Close();
            directPage.Close();

            var canceledTarget = WriteSession(sessionRoot, "ui-canceled", digest);
            chosen = new InventoryService(new UiScanSource()).Scan(sessionRoot).Sessions.Single(s => s.SessionId == "ui-canceled");
            using var canceledBackup = new CleanupDialog(actions.BuildSessionPlan(sessionRoot, [chosen]), sessionRoot, dataHome, [chosen], actions, Path.Combine(fixture, "canceled.zip"));
            canceledBackup.Show(main);
            canceledBackup.RequestBackup();
            var pendingBackup = Application.OpenForms.OfType<BackupDialog>().Single(x => x.Owner == canceledBackup);
            pendingBackup.Close(); // Before queued Shown; exercises cancellation even before the worker starts.
            Pump(() => !Field<bool>(canceledBackup, "_backingUp"));
            Check("canceled backup never enables selected backup deletion", !canceledBackup.ConfirmButtonEnabled && !canceledBackup.BackupReady
                && File.Exists(canceledTarget) && !File.Exists(canceledBackup.ZipPath));
            pendingBackup.Close();
            canceledBackup.Close();

            var processTarget = WriteSession(sessionRoot, "ui-process", digest);
            chosen = new InventoryService(new UiScanSource()).Scan(sessionRoot).Sessions.Single(s => s.SessionId == "ui-process");
            var active = false;
            var processActions = new ActionsPresenter(() => active ? [new ProcessSnapshot(10, "DeepSeek Harness", @"C:\Synthetic\DeepSeek Harness.exe", true)] : []);
            using var processDialog = new CleanupDialog(processActions.BuildSessionPlan(sessionRoot, [chosen]), sessionRoot, dataHome, [chosen], processActions, Path.Combine(fixture, "unused.zip"));
            processDialog.Show(main);
            Check("inactive process initially permits direct confirmation", processDialog.ConfirmButtonEnabled);
            active = true;
            processDialog.RequestConfirm();
            Pump(() => processDialog.RunFinished);
            Check("process becoming active before confirmation blocks deletion", processDialog.LastResult is { Aborted: true } && File.Exists(processTarget));
            processDialog.Close();

            var originalBounds = main.Bounds;
            main.Location = Screen.FromControl(main).WorkingArea.Location;
            using var edge = new AttachmentsForm(dataHome, sessions, actions);
            edge.Show(main);
            CheckCentered("owned modeless window stays within work area at edge", edge, main);
            edge.Close();
            main.Bounds = originalBounds;
            CheckStatusLabels(chosen);
            Check("unselected files and outside sentinel hashes unchanged", keepHashes.All(x => x.Value == Hash(x.Key)));
            Console.WriteLine("WINDOWS_REGRESSION=PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("WINDOWS_REGRESSION=FAIL " + ex.GetType().Name + ":" + ex.Message);
            return 1;
        }
        finally
        {
            foreach (var form in Application.OpenForms.Cast<Form>().ToArray()) form.Close();
        }
    }

    private static string WriteSession(string root, string id, string digest)
    {
        var path = Path.Combine(root, "project", id, "session.v4.jsonl.zstd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = "{\"type\":\"session\",\"id\":\"" + id + "\",\"version\":4,\"createdAt\":1767225600000}\n"
            + "{\"type\":\"session/title\",\"data\":{\"title\":\"Synthetic UI title\"}}\n"
            + "{\"type\":\"user/message\",\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"attachment\":{\"attachmentId\":\"sha256:" + digest + "\",\"name\":\"synthetic-photo.png\",\"mediaType\":\"image/png\"}},{\"type\":\"text\",\"text\":\"Synthetic prompt\"}]}}\n"
            + "{\"type\":\"request/header\",\"data\":{}}\n{\"type\":\"request/context\",\"data\":{}}\n{\"type\":\"todo/write\",\"data\":{\"todos\":[]}}\n"
            + "{\"type\":\"tool/call\",\"data\":{\"name\":\"synthetic_tool\"}}\n"
            + "{\"type\":\"tool/result\",\"data\":{\"message\":{\"role\":\"tool\",\"content\":[{\"type\":\"text\",\"text\":\"synthetic tool result\"}]}}}\n";
        File.WriteAllBytes(path, ZstdFrameDecoder.CompressFrame(Encoding.UTF8.GetBytes(text)));
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void CheckCentered(string label, Form child, Form owner)
    {
        Application.DoEvents(); // WinForms queues Shown for modeless Show(owner).
        var work = Screen.FromRectangle(owner.Bounds).WorkingArea;
        var expected = new Point(Math.Clamp(owner.Left + (owner.Width - child.Width) / 2, work.Left, Math.Max(work.Left, work.Right - child.Width)),
            Math.Clamp(owner.Top + (owner.Height - child.Height) / 2, work.Top, Math.Max(work.Top, work.Bottom - child.Height)));
        Check(label + $" actual={child.Location} expected={expected}", child.Owner == owner && Math.Abs(child.Left - expected.X) <= 1 && Math.Abs(child.Top - expected.Y) <= 1);
    }

    private static void CheckStatusLabels(SessionSummary session)
    {
        var method = typeof(MainForm).GetMethod("StatusText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expected = new[] { "正常", "部分解析", "格式未支持", "损坏", "无法读取" };
        foreach (var status in Enum.GetValues<ParseStatus>())
            Check("parse status label " + status, (string?)method.Invoke(null, [session with { Status = status }]) == expected[(int)status]);
        Check("identity ambiguity label", (string?)method.Invoke(null, [session with { IsAmbiguous = true }]) == "身份歧义");
    }
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void SetField(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Check(string label, bool passed) { if (!passed) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
    private static void Pump(Func<bool> done)
    {
        var start = Environment.TickCount64;
        while (!done() && Environment.TickCount64 - start < 20000) { Application.DoEvents(); Thread.Sleep(10); }
        if (!done()) throw new TimeoutException("UI condition not reached");
    }
}
