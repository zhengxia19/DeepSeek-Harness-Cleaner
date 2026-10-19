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
    private readonly FlowLayoutPanel _filterPanel = new();
    private readonly TextBox _keywordBox = new();
    private readonly TextBox _projectBox = new();
    private readonly TextBox _sessionIdBox = new();
    private readonly ComboBox _dateFieldCombo = new();
    private readonly CheckBox _dateEnabledCheck = new();
    private readonly DateTimePicker _dateStartPicker = new();
    private readonly DateTimePicker _dateEndPicker = new();
    private readonly Button _applyFilterButton = new();
    private readonly DataGridView _sessionGrid = new();
    private readonly RichTextBox _preview = new();
    private readonly FlowLayoutPanel _actionPanel = new();
    private readonly Button _scanButton = new();
    private readonly Button _backupButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _exitButton = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");
    private readonly Font _baseFont;
    private readonly InventoryPresenter _presenter;
    private readonly PreviewPresenter _previewPresenter = new();
    private readonly ActionsPresenter _actionsPresenter;
    private CleanupDialog? _lastCleanupDialog;
    private BackupDialog? _lastBackupDialog;
    private BackupFacts? _lastBackup;
    private ResultsDialog? _lastResultsDialog;
    private ToolStripMenuItem? _attachmentsMenuItem;
    private AttachmentListDialog? _lastAttachmentDialog;
    private readonly string? _scanRootOverride;
    private readonly string? _logDirOverride;
    private readonly bool _persistWindowSettings;
    private string? _effectiveScanRoot;
    private CancellationTokenSource? _previewCts;
    private bool _scanning;

    public MainForm(string? smokeResultPath = null, string? scanRootPath = null, string? logDirPath = null)
    {
        _smokeResultPath = smokeResultPath;
        _scanRootOverride = scanRootPath;
        _logDirOverride = logDirPath;
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

        // N-03：仅正常模式读写用户配置；冒烟/--scan-root 覆盖模式不触碰真实 %LOCALAPPDATA%（避免 CI 污染）。
        _persistWindowSettings = _smokeResultPath is null && _scanRootOverride is null;
        if (_persistWindowSettings)
        {
            ApplySavedWindowGeometry();
            FormClosing += OnFormClosingSaveGeometry;
        }

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

        _sessionGrid.SelectionChanged += OnSessionGridSelectionChanged;
        // N-02：勾选即时提交并刷新删除按钮启用态（预览仍按行选中触发）。
        _sessionGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_sessionGrid.IsCurrentCellDirty)
            {
                _sessionGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _sessionGrid.CellValueChanged += OnSessionGridValueChanged;
        _deleteButton.Enabled = false; // UX-01：未勾选会话时禁用删除（N-02 起按勾选行判定）。
        _backupButton.Enabled = false; // N-05：备份按钮同样按勾选行启用。

        if (_smokeResultPath is not null)
        {
            Shown += OnSmokeShown;
        }
    }

    /// <summary>
    /// N-03：按已保存几何恢复窗口。有保存位置且与任一屏幕工作区相交 → Manual + 位置/大小；
    /// 全出屏（如外接显示器已移除）→ 位置回退 CenterScreen，大小仍恢复；最大化标志独立生效。
    /// </summary>
    private void ApplySavedWindowGeometry()
    {
        var settings = new SettingsStore().Load().Settings;
        var workingAreas = Screen.AllScreens.Select(static screen => screen.WorkingArea).ToArray();
        if (WindowGeometry.TryResolve(settings, workingAreas, out var bounds))
        {
            StartPosition = FormStartPosition.Manual;
            Location = bounds.Location;
        }

        var size = WindowGeometry.RestoreSize(settings);
        Size = new Size(Math.Max(size.Width, MinimumSize.Width), Math.Max(size.Height, MinimumSize.Height));
        if (settings.WindowMaximized == true)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    /// <summary>
    /// N-03：正常退出与关窗均保存窗口几何（先 Load 保留其余设置字段；既有文件损坏/未知 schema
    /// 时 SettingsStore 拒绝覆写，保持既有语义）。最大化时保存 RestoreBounds（正常态几何）。
    /// </summary>
    private void OnFormClosingSaveGeometry(object? sender, FormClosingEventArgs e)
    {
        var store = new SettingsStore();
        var current = store.Load();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _ = store.Save(current.Settings with
        {
            WindowX = bounds.X,
            WindowY = bounds.Y,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = WindowState == FormWindowState.Maximized,
        });
    }

    private void BuildMenu()
    {
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("扫描(&S)", null, OnScanClick));
        // N-06：附件列表（只读展示，无删除入口）。
        _attachmentsMenuItem = new ToolStripMenuItem("附件(&A)", null, OnAttachmentsClick);
        fileMenu.DropDownItems.Add(_attachmentsMenuItem);
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

        _sessionGrid.Dock = DockStyle.Fill;
        _sessionGrid.MultiSelect = true;
        _sessionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // N-02：网格级 ReadOnly 置 false，由列级 ReadOnly=true 锁定除勾选外的全部列。
        // （列级 ReadOnly=false 会被 setter 移除并回退到网格级值，无法覆盖 true。）
        _sessionGrid.ReadOnly = false;
        _sessionGrid.AllowUserToAddRows = false;
        _sessionGrid.AllowUserToDeleteRows = false;
        _sessionGrid.AllowUserToResizeRows = false;
        _sessionGrid.RowHeadersVisible = false;
        _sessionGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _sessionGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        // N-02：勾选列可编辑（其余列继承网格级 ReadOnly），进入单元格即编辑，单击即可切换勾选。
        _sessionGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _sessionGrid.TabIndex = 9;
        // N-02：列序 [勾选][序号][会话 ID][项目][创建时间][日期来源][大小][格式][状态][归档]。
        // 勾选列：删除/备份目标选择，不可排序；序号列：绑定顺序赋值、只读、不可排序。
        _sessionGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "check",
            HeaderText = "选",
            Width = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ReadOnly = false,
        });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "num",
            HeaderText = "序号",
            Width = 50,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "会话 ID", Width = 190 });
        // N-02：project/created/size 三列按内容自适应（AllCells）；绑定后另有全行兜底（EnsureContentColumnWidths）。
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "project",
            HeaderText = "项目",
            Width = 130,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
        });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "created",
            HeaderText = "创建时间",
            Width = 140,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
        });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "origin", HeaderText = "日期来源", Width = 110 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "size",
            HeaderText = "大小",
            Width = 110,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
        });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "format", HeaderText = "格式（代际）", Width = 140 });
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "状态", Width = 110 });
        // N-01：归档标志列（是/—），来自 storages\workspace.json 只读识别；仅展示，不参与删除门禁。
        _sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "archive", HeaderText = "归档", Width = 60 });
        // N-02：除勾选列外全部列只读（序号/数据列不可编辑）。
        foreach (DataGridViewColumn column in _sessionGrid.Columns)
        {
            if (column.Name != "check")
            {
                column.ReadOnly = true;
            }
        }
        _split.Panel1.Controls.Add(_sessionGrid);

        BuildFilterPanel();
        _split.Panel1.Controls.Add(_filterPanel);
    }

    private void BuildFilterPanel()
    {
        _filterPanel.Dock = DockStyle.Top;
        _filterPanel.AutoSize = true;
        _filterPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _filterPanel.WrapContents = true;
        _filterPanel.Padding = new Padding(6, 4, 6, 4);

        _keywordBox.Width = 140;
        _projectBox.Width = 120;
        _sessionIdBox.Width = 140;
        foreach (var box in new[] { _keywordBox, _projectBox, _sessionIdBox })
        {
            box.Margin = new Padding(4, 4, 10, 4);
            box.AutoSize = false;
        }

        _dateFieldCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateFieldCombo.Items.AddRange(new object[] { "创建时间", "文件修改时间（非会话活动）" });
        _dateFieldCombo.SelectedIndex = 0;
        _dateFieldCombo.Width = 190;
        _dateFieldCombo.Margin = new Padding(4, 4, 10, 4);

        _dateEnabledCheck.Text = "启用日期(&E)";
        _dateEnabledCheck.AutoSize = true;
        _dateEnabledCheck.Margin = new Padding(4, 6, 6, 4);

        _dateStartPicker.Format = DateTimePickerFormat.Short;
        _dateEndPicker.Format = DateTimePickerFormat.Short;
        _dateStartPicker.Margin = new Padding(0, 4, 4, 4);
        _dateEndPicker.Margin = new Padding(0, 4, 10, 4);

        _applyFilterButton.Text = "应用筛选(&L)";
        _applyFilterButton.AutoSize = true;
        _applyFilterButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _applyFilterButton.Padding = new Padding(10, 4, 10, 4);
        _applyFilterButton.Margin = new Padding(4, 3, 0, 3);
        _applyFilterButton.Click += (_, _) => ApplyFilterFromUi();

        _keywordBox.TabIndex = 1;
        _projectBox.TabIndex = 2;
        _sessionIdBox.TabIndex = 3;
        _dateFieldCombo.TabIndex = 4;
        _dateEnabledCheck.TabIndex = 5;
        _dateStartPicker.TabIndex = 6;
        _dateEndPicker.TabIndex = 7;
        _applyFilterButton.TabIndex = 8;

        _filterPanel.Controls.AddRange(new Control[]
        {
            new Label { Text = "关键字(&K)", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _keywordBox,
            new Label { Text = "项目(&J)", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _projectBox,
            new Label { Text = "会话 ID(&I)", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _sessionIdBox,
            new Label { Text = "日期字段(&T)", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _dateFieldCombo,
            _dateEnabledCheck,
            new Label { Text = "起", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _dateStartPicker,
            new Label { Text = "止", AutoSize = true, Margin = new Padding(4, 7, 0, 4) },
            _dateEndPicker,
            _applyFilterButton,
        });
    }

    private void BuildPreviewRegion()
    {
        _preview.Dock = DockStyle.Fill;
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
        ConfigureActionButton(_backupButton, "创建备份(&B)", 12);
        ConfigureActionButton(_deleteButton, "删除(&D)", 13);
        ConfigureActionButton(_exitButton, "退出(&X)", 14);

        _scanButton.Click += OnScanClick;
        _backupButton.Click += OnBackupClick;
        _deleteButton.Click += OnDeleteClick;
        _exitButton.Click += OnExitClick;

        _actionPanel.Controls.AddRange(new Control[] { _scanButton, _backupButton, _deleteButton, _exitButton });
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
        UpdateDeleteButtonEnabled();
        _ = RequestPreviewAsync();
    }

    /// <summary>N-02：勾选列值变化即时刷新删除按钮启用态（按勾选行数）。</summary>
    private void OnSessionGridValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _sessionGrid.Columns[e.ColumnIndex].Name == "check")
        {
            UpdateDeleteButtonEnabled();
        }
    }

    /// <summary>N-02/N-05：删除与备份按钮启用条件 = 存在勾选行。</summary>
    private void UpdateDeleteButtonEnabled()
    {
        var anyChecked = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Any(static row => row.Cells["check"].Value is true);
        _deleteButton.Enabled = anyChecked;
        _backupButton.Enabled = anyChecked;
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
            _preview.Text = $"已选择 {rows.Count} 个会话；单选时显示预览（删除按勾选行）。";
            return;
        }

        if (_effectiveScanRoot is null)
        {
            _preview.Text = "请先扫描，再预览会话。";
            return;
        }

        var session = (SessionSummary)rows[0].Tag!;
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

            _preview.Text = PreviewPresenter.Render(outcome);
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

            _statusLabel.Text = _presenter.BuildStatusText(filtered: null);
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

        var request = new FilterRequest(date, _projectBox.Text, _sessionIdBox.Text, _keywordBox.Text);
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
        _statusLabel.Text = _presenter.BuildStatusText(filtered);
    }

    /// <summary>绑定列表：每行 Tag 持有对应 SessionSummary，排序/筛选后选中项仍指向原会话。
    /// N-02：首列勾选（初始未选）、次列序号按绑定顺序 1 起赋值；绑定后对内容列做全行宽度兜底。</summary>
    private void BindSessions(IReadOnlyList<SessionSummary> sessions)
    {
        _sessionGrid.SuspendLayout();
        _sessionGrid.Rows.Clear();
        var num = 1;
        foreach (var session in sessions)
        {
            var index = _sessionGrid.Rows.Add(
                false,
                num++,
                session.SessionId ?? "（未知）",
                session.Project ?? "（未知）",
                FormatDate(session.CreatedAt),
                DateOriginText(session.DateOrigin),
                InventoryPresenter.FormatBytes(session.TotalBytes),
                FormatsText(session),
                StatusText(session),
                session.IsArchived ? "是" : "—");
            _sessionGrid.Rows[index].Tag = session;
        }

        _sessionGrid.ResumeLayout();
        EnsureContentColumnWidths();
        UpdateDeleteButtonEnabled();
    }

    /// <summary>
    /// N-02：AllCells 只按已显示行计算宽度，虚拟化的未显示行可能漏算最长内容；
    /// 绑定后逐行测量文本首选宽度（含表头与单元格内边距），不足则切到手动宽度兜底。
    /// </summary>
    private void EnsureContentColumnWidths()
    {
        foreach (var name in new[] { "project", "created", "size" })
        {
            var column = _sessionGrid.Columns[name]!;
            var preferred = PreferredCellWidth(column.HeaderCell.InheritedStyle, column.HeaderText);
            foreach (DataGridViewRow row in _sessionGrid.Rows)
            {
                var cell = row.Cells[column.Index];
                preferred = Math.Max(preferred, PreferredCellWidth(cell.InheritedStyle, cell.Value as string ?? string.Empty));
            }

            preferred = Math.Max(preferred, column.MinimumWidth);
            if (column.Width < preferred)
            {
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = preferred;
            }
        }
    }

    /// <summary>N-02：单元格文本首选宽度 = MeasureText + 样式内边距 + 6px 边框/余量。</summary>
    private int PreferredCellWidth(DataGridViewCellStyle style, string text)
    {
        var font = style.Font ?? _sessionGrid.Font;
        var measured = TextRenderer.MeasureText(
            text, font, new Size(int.MaxValue / 4, int.MaxValue / 4),
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        return measured.Width + style.Padding.Horizontal + 6;
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
        session.Status + (session.IsAmbiguous ? "（歧义）" : string.Empty);

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        if (_effectiveScanRoot is null)
        {
            _statusLabel.Text = "请先扫描，再删除会话。";
            return;
        }

        // N-02：删除目标 = 勾选行（按列表顺序取 Tag），不再依赖行选中。
        var sessions = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Where(static row => row.Cells["check"].Value is true)
            .Select(static row => (SessionSummary)row.Tag!)
            .ToList();
        if (sessions.Count == 0)
        {
            _statusLabel.Text = "未勾选会话。";
            return;
        }

        var planResult = _actionsPresenter.BuildSessionPlan(_effectiveScanRoot, sessions);
        if (planResult.Plan is null || planResult.Plan.Targets.Count == 0)
        {
            _statusLabel.Text = "无可删除目标：" + (planResult.RootErrorCode
                ?? string.Join("；", planResult.Exclusions.Select(static x => x.Reason)));
            return;
        }

        // 备份清单以数据根（sessions 的父目录，即 .dsh 主目录）为根。
        var dataHome = Path.GetDirectoryName(_effectiveScanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? _effectiveScanRoot;
        // N-05：主窗已有备份且清单覆盖本次全部删除目标时，作为既有备份事实传入对话框（仅展示）；否则按未备份。
        BackupFacts? backupFacts = _lastBackup is { } facts && ManifestCoversPlan(facts.Manifest, dataHome, planResult.Plan)
            ? facts
            : null;
        var dialog = new CleanupDialog(planResult, _effectiveScanRoot, dataHome, sessions, _actionsPresenter, backupFacts);
        dialog.CleanupFinished += OnCleanupFinished;
        _lastCleanupDialog = dialog;
        dialog.Show(this);
        _statusLabel.Text = backupFacts is null
            ? "删除确认对话框已打开：备份可选（建议），确认删除后执行。"
            : "删除确认对话框已打开：已携带主窗备份（已校验），确认删除后执行。";
    }

    /// <summary>N-06：附件列表入口——以当前列表全部会话 + 数据根 attachments 目录后台构建只读索引。</summary>
    private void OnAttachmentsClick(object? sender, EventArgs e)
    {
        if (_effectiveScanRoot is null)
        {
            _statusLabel.Text = "请先扫描，再查看附件。";
            return;
        }

        var sessions = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Select(static row => (SessionSummary)row.Tag!)
            .ToList();
        var dataHome = Path.GetDirectoryName(_effectiveScanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? _effectiveScanRoot;
        var dialog = new AttachmentListDialog(dataHome, sessions);
        _lastAttachmentDialog = dialog;
        dialog.Show(this);
        _statusLabel.Text = "附件列表已打开（只读，后台构建中）。";
    }

    /// <summary>N-05：主窗备份按钮——对勾选会话构建清单并复用 BackupDialog 后台创建+校验；成功后持有备份事实。</summary>
    private void OnBackupClick(object? sender, EventArgs e)
    {
        if (_effectiveScanRoot is null)
        {
            _statusLabel.Text = "请先扫描，再创建备份。";
            return;
        }

        var sessions = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Where(static row => row.Cells["check"].Value is true)
            .Select(static row => (SessionSummary)row.Tag!)
            .ToList();
        if (sessions.Count == 0)
        {
            _statusLabel.Text = "未勾选会话。";
            return;
        }

        // 清单/备份以数据根（.dsh 主目录）为根：条目名须为 sessions/… 类别前缀。
        var dataHome = Path.GetDirectoryName(_effectiveScanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? _effectiveScanRoot;
        var manifestResult = _actionsPresenter.BuildSessionManifest(dataHome, sessions);
        if (manifestResult.Manifest is null)
        {
            _statusLabel.Text = "清单构建失败：" + string.Join("；", manifestResult.Issues.Select(static i => $"{i.Code}:{i.Path}"));
            return;
        }

        var zipPath = Path.Combine(
            Path.GetTempPath(), "dshcleaner-backups", $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.zip");
        var manifest = manifestResult.Manifest;
        var dialog = new BackupDialog(manifest, zipPath, _actionsPresenter);
        _lastBackupDialog = dialog;
        dialog.Completed += (_, _) =>
        {
            if (dialog.State.Stage == BackupStage.Ready)
            {
                // 打开既有 ZIP 复验（SHA-256 逐项），成功才作为删除可携带的备份事实。
                var verified = new BackupService().VerifyZip(zipPath, manifest);
                if (verified.Success && verified.Backup is not null)
                {
                    _lastBackup = new BackupFacts(zipPath, manifest, verified.Backup);
                    _statusLabel.Text = $"备份完成并校验：{zipPath}";
                }
                else
                {
                    _statusLabel.Text = $"备份校验失败：{verified.ErrorCode ?? "UNKNOWN"}，未持有备份事实。";
                }
            }
            else
            {
                _statusLabel.Text = $"备份未完成：{dialog.State.Stage}（{dialog.State.ErrorCode ?? "—"} {dialog.State.Message}）。";
            }
        };
        dialog.Show(this);
        _statusLabel.Text = "正在创建并校验 ZIP 备份…";
    }

    /// <summary>N-05：清单是否覆盖计划全部目标（按数据根相对路径，Windows 大小写不敏感）。</summary>
    private static bool ManifestCoversPlan(BackupManifest manifest, string dataHome, CleanupPlan plan)
    {
        var entries = manifest.Entries
            .Select(static entry => entry.ZipPath.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Targets.All(target =>
            entries.Contains(Path.GetRelativePath(dataHome, target.AbsolutePath)));
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
                _statusLabel.Text = _presenter.BuildStatusText(filtered: null) + "（删除后已刷新）";
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
