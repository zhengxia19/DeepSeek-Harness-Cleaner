using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-20 设置对话框（interfaces.md §4、REQ-019）。打开时经 SettingsStore 读取当前配置：
/// 损坏或未知 schemaVersion 只读回退默认并在对话框内提示，不改动磁盘；
/// 保存走 SettingsStore 原子替换，既有文件损坏/未知 schema 时拒绝覆写并提示。
/// rememberRoots 关闭时根路径输入禁用，且保存后配置不含任何绝对路径。
/// </summary>
public sealed class SettingsDialog : Form
{
    private static readonly DateFilterField[] DateFieldOrder =
    [
        DateFilterField.CreatedAt,
        DateFilterField.LastActivityAt,
        DateFilterField.FileModified,
    ];

    private static readonly string[] DateFieldLabels =
    [
        "创建时间",
        "最近活动时间",
        "文件修改时间（非会话活动）",
    ];

    private readonly SettingsStore _store;
    private readonly CheckBox _rememberRoots = new() { Text = "记住选择的扫描根与备份根" };
    private readonly TextBox _sessionRoot = new();
    private readonly TextBox _desktopRoot = new();
    private readonly TextBox _backupRoot = new();
    private readonly ComboBox _dateField = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _previewMaxItems = new()
    {
        Minimum = 1,
        Maximum = SettingsStore.PreviewMaxItemsLimit,
    };
    private readonly NumericUpDown _windowWidth = new() { Minimum = 320, Maximum = 8192 };
    private readonly NumericUpDown _windowHeight = new() { Minimum = 320, Maximum = 8192 };
    private readonly Label _notice = new() { AutoSize = false, Margin = new Padding(3, 6, 3, 3) };

    public SettingsDialog(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        var loaded = _store.Load();
        var settings = loaded.Settings;

        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 340);

        _rememberRoots.Checked = settings.RememberRoots;
        _sessionRoot.Text = settings.SessionRoot ?? string.Empty;
        _desktopRoot.Text = settings.DesktopRoot ?? string.Empty;
        _backupRoot.Text = settings.BackupRoot ?? string.Empty;
        _dateField.Items.AddRange(DateFieldLabels);
        var dateIndex = Array.IndexOf(DateFieldOrder, settings.DateField);
        _dateField.SelectedIndex = dateIndex >= 0 ? dateIndex : Array.IndexOf(DateFieldOrder, DateFilterField.FileModified);
        _previewMaxItems.Value = ClampToRange(settings.PreviewMaxItems, _previewMaxItems.Minimum, _previewMaxItems.Maximum);
        _windowWidth.Value = ClampToRange(settings.WindowWidth, _windowWidth.Minimum, _windowWidth.Maximum);
        _windowHeight.Value = ClampToRange(settings.WindowHeight, _windowHeight.Minimum, _windowHeight.Maximum);

        _notice.AutoSize = false;
        _notice.MaximumSize = new Size(600, 0);
        _notice.Text = loaded.Status switch
        {
            SettingsLoadStatus.CorruptFallback => $"配置文件损坏，已按默认值显示；原文件未被改动。{loaded.Detail}",
            SettingsLoadStatus.UnknownSchema => $"配置文件版本未知，已按默认值显示；保存不会覆写原文件。{loaded.Detail}",
            SettingsLoadStatus.Default => "尚未保存过设置，当前显示默认值。",
            _ => $"配置文件：{_store.FilePath}",
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        var row = 0;
        AddRow(layout, ref row, string.Empty, _rememberRoots, span: true);
        row = AddRootRow(layout, row, "会话数据根（.dsh）", _sessionRoot);
        row = AddRootRow(layout, row, "桌面数据根", _desktopRoot);
        row = AddRootRow(layout, row, "备份输出根", _backupRoot);
        AddRow(layout, ref row, "日期筛选字段", _dateField, span: false);
        AddRow(layout, ref row, "预览最大条目", _previewMaxItems, span: false);
        AddRow(layout, ref row, "窗口宽度", _windowWidth, span: false);
        AddRow(layout, ref row, "窗口高度", _windowHeight, span: false);
        AddRow(layout, ref row, string.Empty, _notice, span: true);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 10, 3, 0),
        };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += OnSaveClick;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AddRow(layout, ref row, string.Empty, buttons, span: true);

        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

        _rememberRoots.CheckedChanged += (_, _) => UpdateRootsEnabled();
        UpdateRootsEnabled();
    }

    private void UpdateRootsEnabled()
    {
        var enabled = _rememberRoots.Checked;
        foreach (var box in new[] { _sessionRoot, _desktopRoot, _backupRoot })
            box.Enabled = enabled;
        foreach (var control in Controls.OfType<TableLayoutPanel>().First().Controls.OfType<Button>())
        {
            if (control.Tag is TextBox)
                control.Enabled = enabled;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var settings = new AppSettings
        {
            RememberRoots = _rememberRoots.Checked,
            SessionRoot = _sessionRoot.Text.Trim(),
            DesktopRoot = _desktopRoot.Text.Trim(),
            BackupRoot = _backupRoot.Text.Trim(),
            DateField = DateFieldOrder[Math.Clamp(_dateField.SelectedIndex, 0, DateFieldOrder.Length - 1)],
            PreviewMaxItems = (int)_previewMaxItems.Value,
            WindowWidth = (int)_windowWidth.Value,
            WindowHeight = (int)_windowHeight.Value,
        };

        var result = _store.Save(settings);
        if (result.Status == SettingsSaveStatus.Saved)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        MessageBox.Show(
            this,
            result.Detail ?? "设置未能保存，原配置未被覆写。",
            "设置",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static int AddRootRow(TableLayoutPanel layout, int row, string label, TextBox box)
    {
        var browse = new Button { Text = "浏览…", AutoSize = true, Tag = box };
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        box.Dock = DockStyle.Fill;
        layout.Controls.Add(box, 1, row);
        layout.Controls.Add(browse, 2, row);
        return row + 1;
    }

    private static void AddRow(TableLayoutPanel layout, ref int row, string label, Control control, bool span)
    {
        if (label.Length > 0)
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        if (span)
            layout.SetColumnSpan(control, 3);
        layout.Controls.Add(control, 0 + (label.Length > 0 ? 1 : 0), row);
        row++;
    }

    private static decimal ClampToRange(int value, decimal min, decimal max) =>
        Math.Min(Math.Max(value, min), max);
}
