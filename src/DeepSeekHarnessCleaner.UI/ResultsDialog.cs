using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-31 执行结果对话框（REQ-016，TC-16）：显示每项成功/跳过/失败、实际释放字节、
/// 备份位置（完整路径仅在此 UI 出现）与错误码；与 <see cref="OutcomeReport"/> 同源。
/// 打开时把 <see cref="OutcomeReport.BuildLog"/> 的脱敏结构化日志写入日志目录
/// （默认 %LOCALAPPDATA%\DeepSeekHarnessCleaner\logs，可由 --log-dir 注入夹具目录），
/// 日志不含会话正文、路径或自由文本。纯内存渲染，零网络（TC-18）。
/// </summary>
public sealed class ResultsDialog : Form
{
    private readonly TextBox _summaryLabel = new();
    private readonly TextBox _backupLabel = new();
    private readonly TextBox _sessionSummaryLabel = new();
    private readonly DataGridView _itemGrid = new();
    private readonly DataGridView _sessionGrid = new();
    private readonly DataGridView _auditGrid = new();
    private readonly TabControl _resultTabs = new() { Dock = DockStyle.Fill };
    private readonly Button _closeButton = new();
    private readonly Label _logLabel = new();
    private readonly TextBox _logPath = new() { ReadOnly = true, Dock = DockStyle.Fill, ShortcutsEnabled = true };
    private readonly OutcomeReport _report;
    private readonly string? _logDirectory;

    public ResultsDialog(OutcomeReport report, string? logDirectory = null, Action? reviewWorkspace = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _logDirectory = logDirectory;

        Text = "执行结果报告";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(620, 420);

        ConfigureText(_summaryLabel);
        _summaryLabel.Text = _report.Aborted
            ? $"已中止：{_report.AbortCode}（未执行任何删除）"
            : $"删除 {_report.DeletedCount} 个 / 跳过 {_report.SkippedCount} 个 / 失败 {_report.FailedCount} 个，" +
              $"实际释放 {InventoryPresenter.FormatBytes(_report.FreedBytes)}。";
        _summaryLabel.Text = "文件执行：" + _summaryLabel.Text;
        ConfigureText(_sessionSummaryLabel);
        _sessionSummaryLabel.Text = !_report.HasSessionAudit
            ? "会话复查：本调用未提供；不能据文件结果认定会话已彻底清理。"
            : $"会话复查 {_report.Sessions.Count} 个：文件残留 {_report.PhysicalResidueSessionCount}；元数据残留 {_report.MetadataResidueSessionCount}；含未知项 {_report.UnknownSessionCount}。"
                + (_report.HasAuditUnknowns ? "复查存在未知项，不能认定彻底清理。" : "仅为本次观察范围，不代表全部历史数据已清理。");

        ConfigureText(_backupLabel);
        _backupLabel.Text = _report.BackupZipPath is { Length: > 0 } zip
            ? $"备份：{zip}（{_report.BackupEntryCount} 个文件，{InventoryPresenter.FormatBytes(_report.BackupTotalBytes)}，" +
              $"清单签名 {_report.BackupSignature}）"
            : "备份：本次无 ZIP 备份。";
        if (_report.WorkspaceSync is { } sync)
            _backupLabel.Text += Environment.NewLine + _report.WorkspaceSyncSummary
                + (sync.BackupDirectory is null ? "" : Environment.NewLine + "Workspace 原始备份：" + sync.BackupDirectory);

        _itemGrid.Dock = DockStyle.Fill;
        _itemGrid.AllowUserToAddRows = false;
        _itemGrid.AllowUserToDeleteRows = false;
        _itemGrid.ReadOnly = true;
        _itemGrid.RowHeadersVisible = false;
        _itemGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _itemGrid.MultiSelect = false;
        _itemGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        TableHeaders.Apply(_itemGrid);
        _itemGrid.ScrollBars = ScrollBars.Both;
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件", Width = 400 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类别", Width = 110 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "会话", Width = 180 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 90 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "错误码", Width = 160 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "释放字节", Width = 110 });
        foreach (var item in _report.Items)
        {
            _itemGrid.Rows.Add(
                item.DisplayPath,
                item.Category.ToString(),
                item.OwnerSessionId ?? "-",
                StatusText(item.Status),
                item.ErrorCode ?? "-",
                item.FreedBytes.ToString(CultureInfo.InvariantCulture));
        }
        ConfigureAuditGrid(_sessionGrid, ["会话 ID", "会话状态", "残留类别", "原目标仍在", "原目标已无", "原目标未知", "已观察日志", "Workspace 引用", "含未知项"]);
        foreach (var session in _report.Sessions)
            _sessionGrid.Rows.Add(session.SessionId, SessionStateText(session.State),
                session.ResidueCategories.Count == 0 ? session.HasUnknowns ? "未确认" : "未观察到" : string.Join("、", session.ResidueCategories.Select(ResidueText)),
                session.PresentTargets, session.AbsentTargets, session.UnknownTargets, session.ObservedLogCount,
                session.WorkspaceReferenceCount?.ToString(CultureInfo.InvariantCulture) ?? "未知", session.HasUnknowns ? "是" : "否");
        ConfigureAuditGrid(_auditGrid, ["审计异常 / 未知原因", "会话 ID", "证据位置（仅界面）"]);
        _auditGrid.Columns[0].Width = 400;
        _auditGrid.Columns[2].Width = 640;
        foreach (var issue in _report.AuditIssues) _auditGrid.Rows.Add(issue.Code, issue.SessionId ?? "-", issue.EvidencePath ?? "-");
        foreach (var page in new[] { ("文件执行结果", _itemGrid), ("会话复查", _sessionGrid), ("审计异常", _auditGrid) })
        {
            var tab = new TabPage(page.Item1);
            tab.Controls.Add(page.Item2);
            _resultTabs.TabPages.Add(tab);
        }

        _closeButton.Text = "关闭(&L)";
        _closeButton.AutoSize = true;
        _closeButton.Anchor = AnchorStyles.Right;
        _closeButton.TabIndex = 10;
        _closeButton.Click += (_, _) => Close();

        _logLabel.AutoSize = true;
        _logLabel.Dock = DockStyle.Fill;
        _logLabel.Text = "脱敏日志：写入中…";

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(_logLabel, 0, 0);
        footer.Controls.Add(_logPath, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        if (reviewWorkspace is not null)
        {
            var review = new Button { Name = "workspaceReviewButton", Text = "复查 / 恢复 / 重试同步", AutoSize = true };
            review.Click += (_, _) => reviewWorkspace();
            actions.Controls.Add(review);
        }
        actions.Controls.Add(_closeButton);
        footer.Controls.Add(actions, 1, 0);
        footer.SetRowSpan(actions, 2);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.Controls.Add(_summaryLabel, 0, 0);
        layout.Controls.Add(_sessionSummaryLabel, 0, 1);
        layout.Controls.Add(_backupLabel, 0, 2);
        layout.Controls.Add(_resultTabs, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
        Shown += OnShownWriteLog;
    }

    protected override void OnShown(EventArgs e)
    {
        OwnedFormPosition.Center(this);
        base.OnShown(e);
    }

    private static void ConfigureText(TextBox control)
    {
        control.Dock = DockStyle.Fill;
        control.Multiline = true;
        control.ReadOnly = true;
        control.WordWrap = true;
        control.ScrollBars = ScrollBars.Vertical;
        control.BorderStyle = BorderStyle.None;
        control.TabStop = false;
        control.BackColor = SystemColors.Control;
    }

    /// <summary>已写入的脱敏日志文件路径（写入失败为 null）。</summary>
    public string? LogFilePath { get; private set; }

    /// <summary>UI 逐项行数：必须与报告逐项数量一致（冒烟一致性断言）。</summary>
    public int DisplayedItemCount => _itemGrid.Rows.Count;

    public string SummaryText => _summaryLabel.Text;
    public string SessionSummaryText => _sessionSummaryLabel.Text;
    public int DisplayedSessionCount => _sessionGrid.Rows.Count;
    public int DisplayedAuditIssueCount => _auditGrid.Rows.Count;

    public string BackupText => _backupLabel.Text;

    private void OnShownWriteLog(object? sender, EventArgs e)
    {
        try
        {
            var directory = _logDirectory is { Length: > 0 }
                ? _logDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarnessCleaner", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"outcome-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
            File.WriteAllText(path, _report.BuildLog());
            LogFilePath = path;
            _logLabel.Text = "脱敏日志已写入（路径可选中复制）：";
            _logPath.Text = path;
        }
        catch (Exception ex)
        {
            _logLabel.Text = "脱敏日志写入失败：";
            _logPath.Text = ex.Message;
        }
    }

    private static string StatusText(OutcomeItemStatus status) => status switch
    {
        OutcomeItemStatus.Deleted => "已删除",
        OutcomeItemStatus.Skipped => "已跳过",
        OutcomeItemStatus.Failed => "失败",
        _ => status.ToString(),
    };

    private static void ConfigureAuditGrid(DataGridView grid, string[] headers)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.ScrollBars = ScrollBars.Both;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
        TableHeaders.Apply(grid);
        foreach (var header in headers) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = 180 });
    }

    private static string SessionStateText(SessionCleanupState state) => state switch
    {
        SessionCleanupState.PhysicalResidue => "文件残留",
        SessionCleanupState.MetadataResidue => "元数据残留",
        SessionCleanupState.NoObservedResidue => "本次范围未观察到残留",
        _ => "未知",
    };

    private static string ResidueText(OutcomeResidueCategory category) => category switch
    {
        OutcomeResidueCategory.SessionLog => "会话日志",
        OutcomeResidueCategory.DedicatedCache => "专属缓存",
        OutcomeResidueCategory.Attachment => "附件",
        OutcomeResidueCategory.WorkspaceMetadata => "Workspace 元数据",
        _ => "其他物理文件",
    };
}
