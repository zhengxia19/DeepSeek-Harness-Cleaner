using System.Reflection;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static void RunAdvancedChecks(MainForm owner, string fixture)
    {
        CheckWindowState(owner, fixture);
        CheckSelectAll(owner, fixture);
        CheckAttachmentMenu(owner, fixture);
        foreach (var screen in Screen.AllScreens)
        {
            using var probe = new MainForm(persistWindowState: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(screen.WorkingArea.Left + 30, screen.WorkingArea.Top + 30)
            };
            probe.Show();
            Application.DoEvents();
            Console.WriteLine($"ACTUAL_MONITOR={screen.DeviceName} DPI={probe.DeviceDpi} WORKAREA={screen.WorkingArea}");
            CheckGridHeader(Field<DataGridView>(probe, "_sessionGrid"));
            probe.Close();
        }
    }

    private static void CheckWindowState(MainForm owner, string fixture)
    {
        var path = Path.Combine(fixture, "window-state", WindowStateStore.FileName);
        var store = new WindowStateStore(path);
        var notices = new List<string>();
        var work = Screen.FromControl(owner).WorkingArea;
        var desired = new Rectangle(work.Left + 50, work.Top + 50, 900, 600);
        using (var first = new MainForm(windowStateStore: store, windowNotice: notices.Add))
        {
            first.Show(owner);
            Application.DoEvents();
            first.Bounds = desired;
            first.Close();
        }
        Check("window records actual normal outer bounds on close", store.Load().State?.NormalBounds == new WindowRectangle(desired.X, desired.Y, desired.Width, desired.Height));
        var savedHash = Hash(path);
        using (var second = new MainForm(windowStateStore: store, windowNotice: notices.Add))
        {
            second.Show(owner);
            Application.DoEvents();
            Check("next main window restores exact position and size", second.Bounds == desired);
            FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
            second.FormClosing += cancel;
            second.Bounds = new Rectangle(desired.X + 10, desired.Y + 10, 950, 650);
            second.Close();
            Check("canceled close does not write window record", second.Visible && Hash(path) == savedHash);
            second.FormClosing -= cancel;
            second.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            var restored = second.RestoreBounds;
            second.Close();
            Check("maximized close records restore bounds and state", store.Load().State is { WindowState: "Maximized" } state
                && state.NormalBounds == new WindowRectangle(restored.X, restored.Y, restored.Width, restored.Height));
        }
        using (var third = new MainForm(windowStateStore: store, windowNotice: notices.Add))
        {
            third.Show(owner);
            Application.DoEvents();
            Check("maximized state restored", third.WindowState == FormWindowState.Maximized);
            third.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            third.Close();
            Check("minimized close restores as normal next time", store.Load().State?.WindowState == "Normal");
        }
        savedHash = Hash(path);
        using (var locked = new MainForm(windowStateStore: store, windowNotice: notices.Add))
        {
            locked.Show(owner);
            Application.DoEvents();
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) locked.Close();
            Check("save failure still closes and reports without changing old file", !locked.Visible && notices.Count > 0 && Hash(path) == savedHash);
        }
        File.WriteAllText(path, "{bad json");
        using (var corrupt = new MainForm(windowStateStore: store, windowNotice: notices.Add))
        {
            corrupt.Show(owner);
            Application.DoEvents();
            corrupt.Close();
        }
        Check("corrupt window state is preserved", File.ReadAllText(path) == "{bad json");
        var offscreen = new WindowStateStore(Path.Combine(fixture, "offscreen.json"));
        offscreen.Save(new WindowStateRecord(1, new WindowRectangle(9000, 9000, 1300, 900), "Normal", 96));
        using var fitted = new MainForm(windowStateStore: offscreen, windowNotice: notices.Add);
        fitted.Show(owner);
        Application.DoEvents();
        Check("offscreen record is brought into active working area", Screen.FromControl(fitted).WorkingArea.Contains(fitted.Bounds));
        fitted.Close();
    }

    private static void CheckSelectAll(MainForm owner, string fixture)
    {
        var root = Path.Combine(fixture, "selection", ".dsh", "sessions");
        for (var i = 0; i < 1000; i++)
        {
            var id = $"advanced-{i:0000}";
            var path = Path.Combine(root, "project", id, "session.v4.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"type\":\"session\",\"id\":\"" + id + "\",\"version\":4,\"createdAt\":1767225600000}\n");
        }
        using var main = new MainForm(scanRootPath: root, persistWindowState: false);
        main.Show(owner);
        var grid = Field<DataGridView>(main, "_sessionGrid");
        Field<Button>(main, "_scanButton").PerformClick();
        Pump(() => grid.RowCount == 1000);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        HeaderClick(grid);
        timer.Stop();
        var selected = Field<HashSet<SessionSummary>>(main, "_checkedSessions");
        Check("selection header selects all 1000 rows including outside viewport", selected.Count == 1000 && grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells["checked"].Value is true));
        Check("1000-row selection finishes without repeated work", timer.ElapsedMilliseconds < 2000);
        Console.WriteLine("SELECT_1000_MS=" + timer.ElapsedMilliseconds);
        Field<TextBox>(main, "_sessionIdBox").Text = "advanced-0000";
        Field<Button>(main, "_applyFilterButton").PerformClick();
        Check("filter retains existing hidden selection and shows count", grid.RowCount == 1 && selected.Count == 1000
            && Field<ToolStripStatusLabel>(main, "_statusLabel").Text?.Contains("隐藏 999", StringComparison.Ordinal) == true);
        HeaderClick(grid);
        Check("header cancel affects visible set only", selected.Count == 999 && grid.Rows[0].Cells["checked"].Value is false);
        Field<TextBox>(main, "_sessionIdBox").Text = "no-such-session";
        Field<Button>(main, "_applyFilterButton").PerformClick();
        HeaderClick(grid);
        Check("empty filtered header leaves selection unchanged", grid.RowCount == 0 && selected.Count == 999);
        Field<TextBox>(main, "_sessionIdBox").Clear();
        Field<Button>(main, "_applyFilterButton").PerformClick();
        grid.Sort(grid.Columns["id"]!, System.ComponentModel.ListSortDirection.Descending);
        HeaderClick(grid);
        Check("partial selection after sorting selects all", selected.Count == 1000);
        HeaderClick(grid);
        Check("repeated header click cancels all", selected.Count == 0);
        grid.Rows[0].Cells["checked"].Value = true;
        grid.Rows[500].Cells["checked"].Value = true;
        var plan = new ActionsPresenter(() => []).BuildSessionPlan(root, selected.ToArray()).Plan!;
        Check("delete plan matches exact checkbox selection", plan.Targets.Count == 2
            && plan.Targets.Select(t => t.OwnerSessionId).ToHashSet().SetEquals(selected.Select(s => s.SessionId)));
        SetField(main, "_scanning", true);
        HeaderClick(grid);
        Check("header disabled during scan", selected.Count == 2);
        SetField(main, "_scanning", false);
        CheckGridHeader(grid);
        main.Close();
    }

    private static void HeaderClick(DataGridView grid)
    {
        typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(grid, [new DataGridViewCellMouseEventArgs(grid.Columns["checked"]!.Index, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0))]);
    }

    private static void CheckAttachmentMenu(MainForm owner, string fixture)
    {
        var home = Path.Combine(fixture, "opening", ".dsh");
        Directory.CreateDirectory(Path.Combine(home, "attachments"));
        var a = Path.Combine(home, "attachments", "第一份 & 文档.txt");
        var b = Path.Combine(home, "attachments", "第二份 空格.txt");
        File.WriteAllText(a, "synthetic a");
        File.WriteAllText(b, "synthetic b");
        string? launched = null;
        using var form = new AttachmentsForm(home, [], new ActionsPresenter(() => []), new AttachmentOpener(path => launched = path, Path.Combine(fixture, "open-copies")));
        form.Show(owner);
        var list = Field<ListView>(form, "_files");
        Pump(() => list.Items.Count == 2);
        var item = list.Items.Cast<ListViewItem>().Single(i => i.Tag is AttachmentInventoryItem f && f.Candidate.FullPath == b);
        var resolve = typeof(AttachmentsForm).GetMethod("ResolveContextItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = list.Items.Cast<ListViewItem>().Single(i => i.Tag is AttachmentInventoryItem f && f.Candidate.FullPath == a);
        resolve.Invoke(form, [new Point(first.Bounds.Left + 40, first.Bounds.Top + first.Bounds.Height / 2)]);
        Field<ToolStripMenuItem>(form, "_openItem").PerformClick();
        Pump(() => launched == a && !Field<bool>(form, "_opening"));
        launched = null;
        resolve.Invoke(form, [new Point(item.Bounds.Left + 40, item.Bounds.Top + item.Bounds.Height / 2)]);
        Field<ToolStripMenuItem>(form, "_openItem").PerformClick();
        Pump(() => launched is not null);
        Check("attachment menu opens hit row and preserves checkbox state", launched == b && list.CheckedItems.Count == 0);
        resolve.Invoke(form, [new Point(5, list.ClientSize.Height - 10)]);
        Check("blank context cannot open previous row", !Field<ToolStripMenuItem>(form, "_openItem").Enabled);
        Check("attachment ListView owns header drawing only", list.OwnerDraw && !list.Font.Bold);
        SaveScreenshot(form, Path.Combine(fixture, "attachment-headers.png"));
        using var scaled = new Font(list.Font.FontFamily, list.Font.Size * 1.5f);
        list.Font = scaled;
        Application.DoEvents();
        SaveScreenshot(form, Path.Combine(fixture, "attachment-headers-font150.png"));
        Check("font-change header refresh keeps body non-bold", !list.Font.Bold && list.OwnerDraw);
        form.Close();
    }

    private static void CheckGridHeader(DataGridView grid) => Check("grid header bold centered while body font unchanged", grid.ColumnHeadersDefaultCellStyle.Font?.Bold == true
        && grid.ColumnHeadersDefaultCellStyle.Alignment == DataGridViewContentAlignment.MiddleCenter && !grid.Font.Bold);

    private static void SaveScreenshot(Form form, string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
}
