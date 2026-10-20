using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 主窗体：菜单/导航、会话列表区（DataGridView + 过滤面板）、预览区、操作按钮区与状态栏（T-17/T-18）。
/// 布局全部使用 Dock/AutoSize，配合 AutoScaleMode.Font 与 PerMonitorV2 保证任意 DPI 下主操作按钮不被遮挡。
/// T-18：扫描在后台线程执行（UI 不冻结）；列表/统计/筛选与 InventoryService、SessionFilter 结果一致；
/// 每行 Tag 绑定 SessionSummary，多选跟随行对象，排序不会改变选中会话。
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly MenuStrip _menu = new();
    private readonly SplitContainer _split = new();
    private readonly TableLayoutPanel _filterPanel = new();
    private readonly TextBox _keywordBox = new();
    private readonly ComboBox _projectBox = new();
    private readonly TextBox _titleBox = new();
    private readonly TextBox _sessionIdBox = new();
    private readonly ComboBox _dateFieldCombo = new();
    private readonly CheckBox _dateEnabledCheck = new();
    private readonly DateTimePicker _dateStartPicker = new();
    private readonly DateTimePicker _dateEndPicker = new();
    private readonly Button _applyFilterButton = new();
    private readonly Button _resetFilterButton = new();
    private readonly DataGridView _sessionGrid = new();
    private readonly RichTextBox _preview = new();
    private readonly FlowLayoutPanel _actionPanel = new();
    private readonly Button _scanButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _exitButton = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");
    private readonly Font _baseFont;
    private readonly InventoryPresenter _presenter;
    private readonly PreviewPresenter _previewPresenter = new();
    private readonly ActionsPresenter _actionsPresenter;
    private CleanupDialog? _lastCleanupDialog;
    private ResultsDialog? _lastResultsDialog;
    private AttachmentsForm? _attachmentsWindow;
    private readonly string? _scanRootOverride;
    private readonly string? _logDirOverride;
    private string? _effectiveScanRoot;
    private CancellationTokenSource? _previewCts;
    private bool _scanning;
    private bool _preparingDelete;
    private bool _bindingSessions;
    private readonly HashSet<SessionSummary> _checkedSessions = new();
    private readonly System.Windows.Forms.Timer _previewDebounce = new() { Interval = 140 };

    public MainForm(string? smokeResultPath = null, string? scanRootPath = null, string? logDirPath = null,
        WindowStateStore? windowStateStore = null, Action<string>? windowNotice = null, bool persistWindowState = true)
    {
        _smokeResultPath = smokeResultPath;
        _scanRootOverride = scanRootPath;
        _logDirOverride = logDirPath;
        _windowStore = windowStateStore ?? (persistWindowState && smokeResultPath is null ? new WindowStateStore() : null);
        _windowNotice = windowNotice ?? (message => MessageBox.Show(message, "窗口位置记录", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        _presenter = new InventoryPresenter(new UiScanSource(), TimeZoneInfo.Local);
        // 冒烟模式注入“无进程”快照，避免开发机上宿主进程使删除门禁恒阻断；真实模式用真实探测。
        _actionsPresenter = new ActionsPresenter(_smokeResultPath is not null ? static () => [] : null);
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
        TableHeaders.Apply(_sessionGrid);

        // Dock 布局按 z 序从最前到后处理：Fill 控件最先加入，菜单最后加入（最先停靠、占据顶部）。
        Controls.Add(_split);
        Controls.Add(_actionPanel);
        Controls.Add(_statusStrip);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            if (!_bindingSessions && !IsDisposed) _ = RequestPreviewAsync();
        };
        _sessionGrid.SelectionChanged += OnSessionGridSelectionChanged;
        _sessionGrid.ColumnHeaderMouseClick += OnSelectionHeaderClick;
        _sessionGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_sessionGrid.IsCurrentCellDirty && _sessionGrid.CurrentCell?.OwningColumn?.Name == "checked")
                _sessionGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _sessionGrid.CellValueChanged += (_, e) =>
        {
            if (_bindingSessions || e.RowIndex < 0 || _sessionGrid.Columns["checked"] is not { } checkedColumn || e.ColumnIndex != checkedColumn.Index) return;
            var row = _sessionGrid.Rows[e.RowIndex];
            if (row.Tag is not SessionSummary session) return;
            if (row.Cells["checked"].Value is true) _checkedSessions.Add(session);
            else _checkedSessions.Remove(session);
            UpdateSelectionStatus();
        };
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

        var attachmentsMenu = new ToolStripMenuItem("附件(&A)", null, OnAttachmentsClick);
        _menu.Items.AddRange(new ToolStripItem[] { fileMenu, attachmentsMenu, helpMenu });
    }

    private void OnAttachmentsClick(object? sender, EventArgs e)
    {
        if (_effectiveScanRoot is null || _scanning)
        {
            _statusLabel.Text = "请先完成会话扫描，再打开附件管理。";
            return;
        }
        if (_attachmentsWindow is { IsDisposed: false }) { _attachmentsWindow.Activate(); return; }
        var dataHome = Path.GetDirectoryName(_effectiveScanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (dataHome is null) return;
        _attachmentsWindow = new AttachmentsForm(dataHome, _presenter.Sessions, _actionsPresenter);
        _attachmentsWindow.Show(this);
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

        _sessionGrid.Dock = DockStyle.Fill;
        _sessionGrid.MultiSelect = true;
        _sessionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _sessionGrid.ReadOnly = false;
        _sessionGrid.AllowUserToAddRows = false;
        _sessionGrid.AllowUserToDeleteRows = false;
        _sessionGrid.AllowUserToResizeRows = false;
        _sessionGrid.RowHeadersVisible = false;
        _sessionGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _sessionGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _sessionGrid.TabIndex = 9;
        _sessionGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "checked", HeaderText = "选择", Width = 55, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "id", HeaderText = "会话 ID", Width = 190 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "title", HeaderText = "会话标题", Width = 220 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "project", HeaderText = "项目", Width = 130 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "created", HeaderText = "创建时间", Width = 140 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "origin", HeaderText = "日期来源", Width = 110, Visible = false });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "size", HeaderText = "大小", Width = 110 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "format", HeaderText = "格式（代际）", Width = 140, Visible = false });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "archive", HeaderText = "归档", Width = 85 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, Name = "status", HeaderText = "解析状态", Width = 110 });
        _sessionGrid.Columns["title"]!.DisplayIndex = 1;
        _sessionGrid.Columns["project"]!.DisplayIndex = 2;
        _sessionGrid.Columns["created"]!.DisplayIndex = 3;
        _sessionGrid.Columns["size"]!.DisplayIndex = 4;
        _sessionGrid.Columns["id"]!.DisplayIndex = 5;
        _split.Panel1.Controls.Add(_sessionGrid);

        BuildFilterPanel();
        _split.Panel1.Controls.Add(_filterPanel);
    }

    private void BuildFilterPanel()
    {
        _filterPanel.Dock = DockStyle.Top;
        _filterPanel.AutoSize = true;
        _filterPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _filterPanel.ColumnCount = 4;
        _filterPanel.RowCount = 5;
        _filterPanel.Padding = new Padding(6, 4, 6, 4);
        _filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        _filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 5; row++)
            _filterPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _projectBox.DropDownStyle = ComboBoxStyle.DropDown;
        _projectBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _projectBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        _dateFieldCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateFieldCombo.Items.AddRange(new object[] { "创建时间", "文件修改时间（非会话活动）" });
        _dateFieldCombo.SelectedIndex = 0;
        _dateFieldCombo.DropDownWidth = 220;

        foreach (var box in new Control[] { _keywordBox, _titleBox, _projectBox, _sessionIdBox, _dateFieldCombo })
        {
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(2, 3, 6, 3);
            box.MinimumSize = new Size(70, 25);
        }

        _dateEnabledCheck.Text = "启用日期(&E)";
        _dateEnabledCheck.AutoSize = true;
        _dateStartPicker.Format = DateTimePickerFormat.Short;
        _dateEndPicker.Format = DateTimePickerFormat.Short;
        _dateStartPicker.Dock = DockStyle.Fill;
        _dateEndPicker.Dock = DockStyle.Fill;

        _applyFilterButton.Text = "应用筛选(&L)";
        _applyFilterButton.AutoSize = true;
        _applyFilterButton.Click += (_, _) => ApplyFilterFromUi();
        _resetFilterButton.Text = "清空筛选";
        _resetFilterButton.AutoSize = true;
        _resetFilterButton.Click += (_, _) =>
        {
            _keywordBox.Clear();
            _titleBox.Clear();
            _projectBox.Text = string.Empty;
            _sessionIdBox.Clear();
            _dateEnabledCheck.Checked = false;
            ApplyFilterFromUi();
        };

        _keywordBox.TabIndex = 1;
        _projectBox.TabIndex = 2;
        _titleBox.TabIndex = 3;
        _sessionIdBox.TabIndex = 4;
        _dateFieldCombo.TabIndex = 5;
        _dateEnabledCheck.TabIndex = 6;
        _dateStartPicker.TabIndex = 7;
        _dateEndPicker.TabIndex = 8;
        _applyFilterButton.TabIndex = 9;
        _resetFilterButton.TabIndex = 10;

        static Label Caption(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 4, 0, 4),
        };

        _filterPanel.Controls.Add(Caption("关键字"), 0, 0);
        _filterPanel.Controls.Add(_keywordBox, 1, 0);
        _filterPanel.Controls.Add(Caption("项目"), 2, 0);
        _filterPanel.Controls.Add(_projectBox, 3, 0);
        _filterPanel.Controls.Add(Caption("会话标题"), 0, 1);
        _filterPanel.Controls.Add(_titleBox, 1, 1);
        _filterPanel.Controls.Add(Caption("会话 ID"), 2, 1);
        _filterPanel.Controls.Add(_sessionIdBox, 3, 1);
        _filterPanel.Controls.Add(Caption("日期字段"), 0, 2);
        _filterPanel.Controls.Add(_dateFieldCombo, 1, 2);
        _filterPanel.Controls.Add(_dateEnabledCheck, 2, 2);
        _filterPanel.SetColumnSpan(_dateEnabledCheck, 2);
        _filterPanel.Controls.Add(Caption("开始"), 0, 3);
        _filterPanel.Controls.Add(_dateStartPicker, 1, 3);
        _filterPanel.Controls.Add(Caption("结束"), 2, 3);
        _filterPanel.Controls.Add(_dateEndPicker, 3, 3);
        _filterPanel.Controls.Add(_applyFilterButton, 1, 4);
        _filterPanel.Controls.Add(_resetFilterButton, 3, 4);
    }

    private void BuildPreviewRegion()
    {
        _preview.Dock = DockStyle.Fill;
        _preview.WordWrap = false;
        _preview.ScrollBars = RichTextBoxScrollBars.Both;
        _preview.ReadOnly = true;
        _preview.BorderStyle = BorderStyle.None;
        _preview.TabIndex = 10;
        _split.Panel2.Controls.Add(_preview);
    }

    private void BuildActionPanel()
    {
        _actionPanel.Dock = DockStyle.Bottom;
        _actionPanel.AutoSize = true;
        _actionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _actionPanel.WrapContents = false;
        _actionPanel.Padding = new Padding(8, 6, 8, 6);

        ConfigureActionButton(_scanButton, "扫描(&S)", 11);
        ConfigureActionButton(_deleteButton, "删除(&D)", 12);
        ConfigureActionButton(_exitButton, "退出(&X)", 13);

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

    private void OnSessionGridSelectionChanged(object? sender, EventArgs e)
    {
        if (_bindingSessions) return;
        _deleteButton.Enabled = _checkedSessions.Count > 0 || _sessionGrid.SelectedRows.Count > 0;
        // SelectionChanged can run before CurrentCell moves off the checkbox column.
        // Debounce the final selection; a stale cell must not suppress its preview or clearing.
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>
    /// T-19：选中单个会话时触发后台受限预览。重选先取消旧请求；渲染前核对
    /// 请求令牌与当前选中会话仍是同一对象，取消/重选不会把旧会话内容串进预览。
    /// </summary>
    private async Task RequestPreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var rows = _sessionGrid.SelectedRows.Cast<DataGridViewRow>().ToList();
        if (rows.Count == 0)
        {
            _preview.Text = string.Empty;
            return;
        }

        if (rows.Count > 1)
        {
            _preview.Text = $"已选择 {rows.Count} 个会话；单选时显示预览（删除按整会话选择）。";
            return;
        }

        if (_effectiveScanRoot is null)
        {
            _preview.Text = "请先扫描，再预览会话。";
            return;
        }

        if (rows[0].Tag is not SessionSummary session)
        {
            _preview.Clear();
            return;
        }
        _preview.Text = "预览加载中…";
        try
        {
            var outcome = await _previewPresenter.PreviewAsync(_effectiveScanRoot, session, cts.Token);
            if (!ReferenceEquals(_previewCts, cts))
            {
                return; // 已被更新的预览请求取代。
            }

            var current = _sessionGrid.SelectedRows.Count == 1
                ? _sessionGrid.SelectedRows[0].Tag as SessionSummary
                : null;
            if (!ReferenceEquals(current, session))
            {
                return; // 当前选中会话已变化，丢弃旧结果（不串会话）。
            }

            var rendered = PreviewPresenter.Render(outcome);
            const int maxVisibleCharacters = 250_000;
            _preview.Text = rendered.Length <= maxVisibleCharacters
                ? rendered
                : rendered[..maxVisibleCharacters] + Environment.NewLine + "（显示已限制在 250,000 字符；原始日志未修改）";
        }
        catch (OperationCanceledException)
        {
            // 旧请求被新选择取消，保持新请求的显示。
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _preview.Text = "预览失败：" + ex.Message;
            }
        }
    }

    private async void OnScanClick(object? sender, EventArgs e)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        _scanButton.Enabled = false;
        _statusLabel.Text = "扫描中…";
        try
        {
            var root = ResolveScanRoot();
            if (root is null)
            {
                _statusLabel.Text = _rootErrorText ?? "数据根不可用。";
                return;
            }

            var result = await _presenter.ScanAsync(root);
            if (result.RootErrorCode is null)
            {
                _effectiveScanRoot = root;
                BindSessions(result.Sessions);
            }

            UpdateSelectionStatus();
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanning = false;
            _scanButton.Enabled = true;
            if (_smokeResultPath is not null)
            {
                _smokeScanFinished = true;
            }
        }
    }

    private string? _rootErrorText;

    private string? ResolveScanRoot()
    {
        if (_scanRootOverride is not null)
        {
            return _scanRootOverride;
        }

        var roots = new RootLocator().Resolve();
        if (!roots.DshProbe.IsValid)
        {
            _rootErrorText = $"数据根不可用：{roots.DshProbe.ErrorCode}（{roots.DshProbe.HomePath}）";
            return null;
        }

        return roots.SessionRoot;
    }

    private void ApplyFilterFromUi()
    {
        if (_presenter.LastResult is null)
        {
            _statusLabel.Text = "请先扫描，再应用筛选。";
            return;
        }

        DateFilter? date = null;
        if (_dateEnabledCheck.Checked)
        {
            var field = _dateFieldCombo.SelectedIndex == 0 ? DateFilterField.CreatedAt : DateFilterField.FileModified;
            date = new DateFilter(
                field,
                DateOnly.FromDateTime(_dateStartPicker.Value.Date),
                DateOnly.FromDateTime(_dateEndPicker.Value.Date));
        }

        var request = new FilterRequest(date, _projectBox.Text, _sessionIdBox.Text, _keywordBox.Text, _titleBox.Text);
        IReadOnlyList<SessionSummary> filtered;
        try
        {
            filtered = _presenter.ApplyFilter(request);
        }
        catch (ArgumentException ex)
        {
            _statusLabel.Text = "筛选无效：" + ex.Message;
            return;
        }

        BindSessions(filtered);
        UpdateSelectionStatus();
    }

    /// <summary>绑定列表：每行 Tag 持有对应 SessionSummary，排序/筛选后选中项仍指向原会话。</summary>
    private void BindSessions(IReadOnlyList<SessionSummary> sessions)
    {
        _bindingSessions = true;
        var projectInput = _projectBox.Text;
        _projectBox.BeginUpdate();
        _projectBox.Items.Clear();
        _projectBox.Items.AddRange(_presenter.Sessions.Select(static s => s.Project).OfType<string>().Where(static p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        _projectBox.Text = projectInput;
        _projectBox.EndUpdate();
        _previewDebounce.Stop();
        _previewCts?.Cancel();
        _sessionGrid.SuspendLayout();
        _sessionGrid.Rows.Clear();
        var validSessions = _presenter.Sessions.ToHashSet();
        _checkedSessions.RemoveWhere(session => !validSessions.Contains(session));
        foreach (var session in sessions)
        {
            var index = _sessionGrid.Rows.Add(
                _checkedSessions.Contains(session),
                session.SessionId ?? "（未知）",
                session.Title ?? "（未命名）",
                session.Project ?? "（未知）",
                FormatDate(session.CreatedAt),
                DateOriginText(session.DateOrigin),
                InventoryPresenter.FormatBytes(session.TotalBytes),
                FormatsText(session),
                session.IsArchived switch { true => "已归档", false => "未归档", _ => "未知" },
                StatusText(session));
            _sessionGrid.Rows[index].Tag = session;
        }

        _sessionGrid.ClearSelection();
        _sessionGrid.ResumeLayout();
        _bindingSessions = false;
        UpdateSelectionStatus();
        _deleteButton.Enabled = _checkedSessions.Count > 0;
        _preview.Clear();
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value is null ? "（未知）" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string DateOriginText(DateOrigin origin) => origin switch
    {
        DateOrigin.Header => "header",
        DateOrigin.Event => "事件",
        DateOrigin.FileMtime => "文件修改时间",
        _ => "未知",
    };

    private static string FormatsText(SessionSummary session) =>
        string.Join("，", session.Generations.Select(static g => $"v{g.Generation}{(g.Compressed ? " zstd" : " 明文")}"));

    private static string StatusText(SessionSummary session) =>
        session.IsAmbiguous ? "身份歧义" : session.Status switch
        {
            ParseStatus.Valid => "正常",
            ParseStatus.Partial => "部分解析",
            ParseStatus.Unsupported => "格式未支持",
            ParseStatus.Corrupt => "损坏",
            ParseStatus.Unreadable => "无法读取",
            _ => "未知",
        };

    private async void OnDeleteClick(object? sender, EventArgs e)
    {
        if (_preparingDelete) return;
        if (_lastCleanupDialog is { IsDisposed: false, Visible: true, RunFinished: false }) { _lastCleanupDialog.Activate(); return; }
        if (_effectiveScanRoot is null)
        {
            _statusLabel.Text = "请先扫描，再删除会话。";
            return;
        }

        var sessions = _checkedSessions.Count > 0
            ? _checkedSessions.ToList()
            : _sessionGrid.SelectedRows.Cast<DataGridViewRow>()
                .Select(static row => row.Tag as SessionSummary)
                .OfType<SessionSummary>()
                .ToList();
        if (sessions.Count == 0)
        {
            _statusLabel.Text = "未选择会话。";
            return;
        }

        var sessionRoot = _effectiveScanRoot;
        CleanupPlanResult planResult;
        try
        {
            _preparingDelete = true;
            _deleteButton.Enabled = false;
            _statusLabel.Text = "正在复验会话文件和身份…";
            planResult = await Task.Run(() => _actionsPresenter.BuildSessionPlan(sessionRoot, sessions));
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _statusLabel.Text = "删除清单复验失败：" + ex.Message;
            return;
        }
        finally
        {
            _preparingDelete = false;
            if (!IsDisposed) _deleteButton.Enabled = _checkedSessions.Count > 0 || _sessionGrid.SelectedRows.Count > 0;
        }
        if (IsDisposed) return;
        if (planResult.Plan is null || planResult.Plan.Targets.Count == 0)
        {
            _statusLabel.Text = "无可删除目标：" + (planResult.RootErrorCode
                ?? string.Join("；", planResult.Exclusions.Select(static x => x.Reason)));
            return;
        }

        var zipPath = Path.Combine(
            Path.GetTempPath(), "dshcleaner-backups", $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        // 备份清单以数据根（sessions 的父目录，即 .dsh 主目录）为根。
        var dataHome = Path.GetDirectoryName(sessionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? sessionRoot;
        var dialog = new CleanupDialog(planResult, sessionRoot, dataHome, sessions, _actionsPresenter, zipPath);
        dialog.CleanupFinished += OnCleanupFinished;
        _lastCleanupDialog = dialog;
        dialog.Show(this);
        _statusLabel.Text = "删除确认对话框已打开：核对文件后确认删除，备份可选。";
    }

    private async void OnCleanupFinished(object? sender, CleanupExecutionResult result)
    {
        if (_effectiveScanRoot is null)
        {
            return;
        }

        _statusLabel.Text = result.Aborted
            ? $"删除被阻断：{result.AbortCode}。"
            : $"删除完成：{result.DeletedCount} 个文件 / 跳过 {result.SkippedCount} / 失败 {result.FailedCount}。正在刷新清单…";
        try
        {
            var refresh = await _presenter.ScanAsync(_effectiveScanRoot);
            if (refresh.RootErrorCode is null)
            {
                BindSessions(refresh.Sessions);
                UpdateSelectionStatus();
                _statusLabel.Text += "（删除后已刷新）";
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "刷新失败：" + ex.Message;
        }

        // T-31：打开不泄露正文的执行结果报告（逐项状态/释放字节/备份位置/错误码 + 脱敏日志）。
        if (_lastCleanupDialog is { } source)
        {
            var report = _actionsPresenter.BuildReport(
                source.Plan, source.Backup, result, source.ZipPath);
            var results = new ResultsDialog(report, _logDirOverride);
            _lastResultsDialog = results;
            results.Show(this);
            _statusLabel.Text += " 结果报告已打开（含脱敏日志）。";
        }
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
