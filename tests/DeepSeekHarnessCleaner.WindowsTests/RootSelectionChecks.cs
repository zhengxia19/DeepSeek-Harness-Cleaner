using System.Reflection;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static int RunRootSelectionChecks()
    {
        var fixture = Path.GetFullPath(Path.Combine(".artifacts", "T06", "ui-" + Guid.NewGuid().ToString("N")));
        var account = Path.Combine(fixture, "account");
        var homeA = Path.Combine(account, ".dsh");
        var homeB = Path.Combine(fixture, "other-home");
        var desktop = Path.Combine(fixture, "desktop");
        foreach (var home in new[] { homeA, homeB })
            foreach (var child in new[] { "sessions", "storages", "attachments" }) Directory.CreateDirectory(Path.Combine(home, child));
        Directory.CreateDirectory(Path.Combine(desktop, "Cache"));
        Directory.CreateDirectory(Path.Combine(desktop, "logs"));
        var a = WriteSession(Path.Combine(homeA, "sessions"), "same-id", new string('a', 64));
        var b = WriteSession(Path.Combine(homeB, "sessions"), "same-id", new string('b', 64));
        var sentinel = Path.Combine(fixture, "outside-sentinel.txt");
        File.WriteAllText(sentinel, "synthetic outside sentinel");
        var before = new[] { a, b, sentinel }.ToDictionary(x => x, Hash);
        var locator = new RootLocator(folder => folder == Environment.SpecialFolder.UserProfile ? account : Path.Combine(fixture, "appdata"), _ => homeB);
        var store = new SettingsStore(Path.Combine(fixture, "settings.json"));
        using var main = new MainForm(persistWindowState: false, rootLocator: locator, settingsStore: store);
        try
        {
            using (var picker = new RootSelectionDialog(locator, AppSettings.Default))
            {
                picker.Show(main);
                Check("multiple valid data roots require explicit selection", picker.SelectedDshHome is null && !Field<Button>(picker, "_ok").Enabled);
                picker.Close();
            }
            // Exercise the same manual path admission and refresh used after FolderBrowserDialog returns.
            using (var picker = new RootSelectionDialog(locator, AppSettings.Default))
            {
                SetField(picker, "_manualDesktop", desktop);
                picker.GetType().GetMethod("RefreshCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
                SelectRoot(Field<ListBox>(picker, "_desktopCandidates"), desktop);
                Check("desktop selection alone does not confirm desktop root", picker.SelectedDesktopHome is null);
                Field<CheckBox>(picker, "_desktopConfirmed").Checked = true;
                Check("desktop root confirmed independently", picker.SelectedDesktopHome == desktop && picker.SelectedDshHome is null);
                SetField(picker, "_manualDsh", homeA);
                picker.GetType().GetMethod("RefreshCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
                Check("manual data root retained and desktop reconfirmation required", picker.SelectedDshHome == homeA && picker.SelectedDesktopHome is null);
            }
            main.Show();
            var grid = Field<DataGridView>(main, "_sessionGrid");
            ChooseMainRoot(main, homeA, desktop, true);
            Check("root choice does not auto scan", grid.Rows.Count == 0 && Field<string?>(main, "_effectiveScanRoot") is null);
            var remembered = store.Load().Settings;
            Check("remember stores sessions child and independent desktop", remembered.RememberRoots && remembered.SessionRoot == Path.Combine(homeA, "sessions") && remembered.DesktopRoot == desktop);
            using (var picker = new RootSelectionDialog(locator, remembered))
            {
                Check("saved path participates but multiple roots still need selection", picker.SelectedDshHome is null);
                Check("saved source is visible", Field<ListBox>(picker, "_dshCandidates").Items.Cast<object>().Any(x => x.ToString()!.Contains("已记忆", StringComparison.Ordinal)));
            }
            Field<Button>(main, "_scanButton").PerformClick();
            Pump(() => grid.Rows.Count == 1 && !Field<bool>(main, "_scanning"));
            grid.Rows[0].Cells["checked"].Value = true;
            grid.CurrentCell = grid.Rows[0].Cells["id"];
            grid.Rows[0].Selected = true;
            Pump(() => Field<RichTextBox>(main, "_preview").Text.Contains("synthetic tool result", StringComparison.Ordinal));
            Field<Button>(main, "_deleteButton").PerformClick();
            Pump(() => Field<CleanupDialog?>(main, "_lastCleanupDialog") is { Visible: true });
            var oldPlan = Field<CleanupDialog>(main, "_lastCleanupDialog");
            InvokeChoose(main);
            Check("open delete confirmation prevents root change", Field<string?>(main, "_confirmedDshHome") == homeA && !oldPlan.IsDisposed);
            oldPlan.RequestCancel();
            ChooseMainRoot(main, homeB, null, false);
            Check("root switch invalidates scan and visible rows", Field<string?>(main, "_effectiveScanRoot") is null && grid.Rows.Count == 0);
            Check("root switch clears checked selection and preview", Field<HashSet<SessionSummary>>(main, "_checkedSessions").Count == 0 && Field<RichTextBox>(main, "_preview").Text.Length == 0);
            Check("root switch discards old cleanup and disables delete", Field<CleanupDialog?>(main, "_lastCleanupDialog") is null && oldPlan.IsDisposed && !Field<Button>(main, "_deleteButton").Enabled);
            Check("root switch discards presenter facts", Field<InventoryPresenter>(main, "_presenter").LastResult is null);
            Check("desktop does not silently follow session root", Field<string?>(main, "_confirmedDesktopHome") is null);
            var forgotten = store.Load().Settings;
            Check("turning remember off clears stored paths", !forgotten.RememberRoots && forgotten.SessionRoot is null && forgotten.DesktopRoot is null);
            main.BeginInvoke(new Action(() => Application.OpenForms.OfType<RootSelectionDialog>().Single().Close()));
            InvokeChoose(main);
            Check("cancel preserves confirmed root", Field<string?>(main, "_confirmedDshHome") == homeB);
            Field<Button>(main, "_scanButton").PerformClick();
            Pump(() => grid.Rows.Count == 1 && !Field<bool>(main, "_scanning"));
            Check("same logical ID scans only new root", ((SessionSummary)grid.Rows[0].Tag!).Generations.All(x => x.FullPath.StartsWith(homeB, StringComparison.OrdinalIgnoreCase)));
            // No plan from root A can be opened while root B facts are being replaced.
            SetField(main, "_scanning", true);
            main.GetType().GetMethod("OnDeleteClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object?[] { main, EventArgs.Empty });
            Check("delete blocked while scanning", Field<CleanupDialog?>(main, "_lastCleanupDialog") is null);
            SetField(main, "_scanning", false);
            SaveScreenshot(main, Path.Combine(fixture, "root-b-main.png"));
            foreach (var file in before) Check("protected hash unchanged " + Path.GetFileName(file.Key), Hash(file.Key) == file.Value);
            Console.WriteLine("T06_FIXTURE " + fixture);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("T06_FIXTURE " + fixture); return 1; }
        finally { main.Close(); }
    }

    private static void SelectRoot(ListBox list, string path)
    {
        var item = list.Items.Cast<object>().Single(x => x.ToString()!.StartsWith(path + " |", StringComparison.OrdinalIgnoreCase));
        list.SelectedItem = item;
    }

    private static void InvokeChoose(MainForm main) => main.GetType().GetMethod("OnChooseRoots", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(main, new object?[] { main, EventArgs.Empty });

    private static void ChooseMainRoot(MainForm main, string home, string? desktop, bool remember)
    {
        main.BeginInvoke(new Action(() =>
        {
            var picker = Application.OpenForms.OfType<RootSelectionDialog>().Single();
            if (desktop is not null)
            {
                SetField(picker, "_manualDesktop", desktop);
                picker.GetType().GetMethod("RefreshCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
                SelectRoot(Field<ListBox>(picker, "_desktopCandidates"), desktop);
                Field<CheckBox>(picker, "_desktopConfirmed").Checked = true;
            }
            else Field<CheckBox>(picker, "_desktopConfirmed").Checked = false;
            SelectRoot(Field<ListBox>(picker, "_dshCandidates"), home);
            Field<CheckBox>(picker, "_remember").Checked = remember;
            SaveScreenshot(picker, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(home))!, "root-selector.png"));
            Field<Button>(picker, "_ok").PerformClick();
        }));
        InvokeChoose(main);
    }
}
