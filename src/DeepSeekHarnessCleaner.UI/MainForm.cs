using System.Drawing;
using System.Windows.Forms;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 主窗体：菜单/导航、会话列表区、预览区、操作按钮区与状态栏的单窗口布局（T-17，可无数据）。
/// 布局全部使用 Dock/AutoSize，配合 AutoScaleMode.Font 与 PerMonitorV2 保证任意 DPI 下主操作按钮不被遮挡。
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly MenuStrip _menu = new();
    private readonly SplitContainer _split = new();
    private readonly ListView _sessionList = new();
    private readonly RichTextBox _preview = new();
    private readonly FlowLayoutPanel _actionPanel = new();
    private readonly Button _scanButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _exitButton = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");
    private readonly Font _baseFont;

    public MainForm(string? smokeResultPath = null)
    {
        _smokeResultPath = smokeResultPath;
        _baseFont = SystemFonts.MessageBoxFont ?? DefaultFont;
        Font = _baseFont;
        AutoScaleMode = AutoScaleMode.Font;

        Text = "DeepSeek Harness Cleaner";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 680);
        MinimumSize = new Size(640, 420);

        BuildMenu();
        BuildListRegion();
        BuildPreviewRegion();
        BuildActionPanel();
        BuildStatusBar();

        // Dock 布局按 z 序从最前到后处理：Fill 控件最先加入，菜单最后加入（最先停靠、占据顶部）。
        Controls.Add(_split);
        Controls.Add(_actionPanel);
        Controls.Add(_statusStrip);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _sessionList.SelectedIndexChanged += OnSessionListSelectionChanged;
        _deleteButton.Enabled = false; // UX-01：未选择会话时禁用删除。

        if (_smokeResultPath is not null)
        {
            Shown += OnSmokeShown;
        }
    }

    private void BuildMenu()
    {
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("扫描(&S)", null, OnScanClick));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出(&X)", null, OnExitClick));

        var helpMenu = new ToolStripMenuItem("帮助(&H)");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("关于(&A)", null, OnAboutClick));

        _menu.Items.AddRange(new ToolStripItem[] { fileMenu, helpMenu });
    }

    private void BuildListRegion()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        // 先给容器显式初始尺寸再设 MinSize/SplitterDistance：
        // SplitContainer 默认宽约 150，直接设 460 会触发“距离不在合法区间”异常。
        _split.Size = new Size(1100, 600);
        _split.Panel1MinSize = 160;
        _split.Panel2MinSize = 200;
        _split.SplitterDistance = 460;

        _sessionList.Dock = DockStyle.Fill;
        _sessionList.View = View.Details;
        _sessionList.FullRowSelect = true;
        _sessionList.HideSelection = false;
        _sessionList.MultiSelect = true;
        _sessionList.TabIndex = 1;
        _sessionList.Columns.Add("会话 ID", 240);
        _sessionList.Columns.Add("项目", 200);
        _sessionList.Columns.Add("创建时间", 150);
        _sessionList.Columns.Add("大小", 100);
        _sessionList.Columns.Add("状态", 120);
        _split.Panel1.Controls.Add(_sessionList);
    }

    private void BuildPreviewRegion()
    {
        _preview.Dock = DockStyle.Fill;
        _preview.ReadOnly = true;
        _preview.BorderStyle = BorderStyle.None;
        _preview.TabIndex = 2;
        _split.Panel2.Controls.Add(_preview);
    }

    private void BuildActionPanel()
    {
        _actionPanel.Dock = DockStyle.Bottom;
        _actionPanel.AutoSize = true;
        _actionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _actionPanel.WrapContents = false;
        _actionPanel.Padding = new Padding(8, 6, 8, 6);

        ConfigureActionButton(_scanButton, "扫描(&S)", 3);
        ConfigureActionButton(_deleteButton, "删除(&D)", 4);
        ConfigureActionButton(_exitButton, "退出(&X)", 5);

        _scanButton.Click += OnScanClick;
        _deleteButton.Click += OnDeleteClick;
        _exitButton.Click += OnExitClick;

        _actionPanel.Controls.AddRange(new Control[] { _scanButton, _deleteButton, _exitButton });
    }

    private static void ConfigureActionButton(Button button, string text, int tabIndex)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(14, 6, 14, 6);
        button.Margin = new Padding(6, 0, 0, 0);
        button.Text = text;
        button.TabIndex = tabIndex;
    }

    private void BuildStatusBar()
    {
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Dock = DockStyle.Bottom;
    }

    private void OnSessionListSelectionChanged(object? sender, EventArgs e)
    {
        _deleteButton.Enabled = _sessionList.SelectedItems.Count > 0;
    }

    private void OnScanClick(object? sender, EventArgs e)
    {
        _statusLabel.Text = "扫描功能将在 T-18 接入。";
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        _statusLabel.Text = "删除流程将在 T-20 之后的任务接入。";
    }

    private void OnExitClick(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        MessageBox.Show(
            this,
            "DeepSeek Harness Cleaner\n用于受控目录中 DSH 会话数据的查看与清理。",
            "关于",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
