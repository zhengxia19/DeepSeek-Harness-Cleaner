using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 删除确认对话框（T-30，N-04/N-05 调整；N-07 自适应布局）：展示最终删除清单（逐文件路径/大小）、排除项与进程警告、备份状态；
/// 备份事实由主窗（N-05）可选携带，本对话框仅展示、不再创建备份；
/// 备份可选（建议）：“确认删除”初始即可用；确认后后台执行删除（可取消），完成触发 <see cref="CleanupFinished"/>。
/// 取消（未开始执行时）直接关闭，不产生任何删除。
/// N-07：绝对坐标改 Dock/TableLayoutPanel——标签区顶部、清单 Dock=Fill（列宽 FillWeight 随窗伸缩）、
/// 按钮行底部；设定 MinimumSize，缩放/最大化时清单与按钮不遮挡。数据流与事件不变。
/// </summary>
public sealed class CleanupDialog : Form
{
    private readonly DataGridView _targetGrid = new();
    private readonly Label _planLabel = new();
    private readonly Label _warningLabel = new();
    private readonly Label _backupLabel = new();
    private readonly Label _resultLabel = new();
    private readonly Button _confirmButton = new();
    private readonly Button _cancelButton = new();
    private readonly ActionsPresenter _presenter;
    private readonly CleanupPlan _plan;
    private readonly IReadOnlyList<PlanExclusion> _exclusions;
    private readonly string _root;
    private readonly BackupFacts? _backupFacts;
    private CancellationTokenSource? _cts;
    private bool _running;

    public CleanupDialog(
        CleanupPlanResult planResult,
        string root,
        string dataHome,
        IReadOnlyList<SessionSummary> sessions,
        ActionsPresenter presenter,
        BackupFacts? backupFacts)
    {
        _plan = planResult.Plan!;
        _exclusions = planResult.Exclusions;
        _root = root;
        _backupFacts = backupFacts;
        _presenter = presenter;

        Text = "删除确认：会话原始文件";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(480, 320);
        ClientSize = new Size(640, 420);

        _planLabel.AutoSize = true;
        _planLabel.Margin = new Padding(6, 6, 6, 0);
        _planLabel.Text = $"会话 {sessions.Count} 个；待删除文件 {_plan.Targets.Count} 个" +
            $"（合计 {InventoryPresenter.FormatBytes(_plan.Targets.Sum(static t => t.ExpectedLength))}）" +
            (_exclusions.Count > 0 ? $"；排除项 {_exclusions.Count} 个" : string.Empty);

        var probe = presenter.ProbeProcesses();
        _warningLabel.AutoSize = true;
        _warningLabel.Margin = new Padding(6, 4, 6, 0);
        _warningLabel.ForeColor = probe.AllowsWrite ? Color.FromKnownColor(KnownColor.ControlText) : Color.Firebrick;
        _warningLabel.Text = probe.AllowsWrite
            ? "进程状态：Harness 未运行，可执行删除。"
            : $"警告：进程状态 {probe.State}（{(probe.ErrorCode ?? string.Join("；", probe.RiskNotes))}），删除将被阻断。";

        _backupLabel.AutoSize = true;
        _backupLabel.Margin = new Padding(6, 4, 6, 6);
        // N-05：备份事实由主窗携带（仅展示）；未携带时保持“未备份（可选，建议）”。
        _backupLabel.Text = backupFacts is null
            ? "备份状态：未备份（可选，建议）。"
            : $"备份状态：已备份并校验（{backupFacts.Manifest.Entries.Count} 个文件，" +
              $"{InventoryPresenter.FormatBytes(backupFacts.Verified.TotalBytes)}）。可确认删除。";

        // N-07：清单随窗伸缩——Dock=Fill + 列 FillWeight（相对列吸收多余宽度）。
        _targetGrid.Dock = DockStyle.Fill;
        _targetGrid.ReadOnly = true;
        _targetGrid.AllowUserToAddRows = false;
        _targetGrid.AllowUserToDeleteRows = false;
        _targetGrid.AllowUserToResizeRows = false;
        _targetGrid.MultiSelect = false;
        _targetGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _targetGrid.RowHeadersVisible = false;
        _targetGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "文件（相对数据根）", FillWeight = 470 });
        _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小", FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "category", HeaderText = "类别", FillWeight = 60 });
        foreach (var target in _plan.Targets)
        {
            var relative = Path.GetRelativePath(_root, target.AbsolutePath);
            _targetGrid.Rows.Add(
                relative,
                InventoryPresenter.FormatBytes(target.ExpectedLength),
                target.Category.ToString());
        }

        _confirmButton.Text = "确认删除(&D)";
        _confirmButton.AutoSize = true;
        _confirmButton.Margin = new Padding(0, 0, 12, 0);
        _confirmButton.TabIndex = 21;
        // N-04（D1/D4）：备份可选，确认按钮初始可用；确认本身仍是删除的必需门禁。
        _confirmButton.Click += OnConfirmClick;

        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.TabIndex = 22;
        _cancelButton.Click += OnCancelClick;

        _resultLabel.AutoSize = true;
        _resultLabel.Margin = new Padding(6, 4, 6, 0);
        _resultLabel.Text = string.Empty;

        // N-07：三区结构——顶部标签区（TableLayoutPanel 自适应行高）、中部清单 Fill、底部结果+按钮行。
        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(6, 4, 6, 2),
        };
        topPanel.Controls.AddRange(new Control[] { _planLabel, _warningLabel, _backupLabel });

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 6, 0),
        };
        buttonRow.Controls.Add(_cancelButton);
        buttonRow.Controls.Add(_confirmButton);

        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 0, 6),
        };
        bottomPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomPanel.Controls.Add(_resultLabel, 0, 0);
        bottomPanel.Controls.Add(buttonRow, 0, 1);

        // Dock 计算按 Z 序反向：先加 Fill 控件，再加 Top/Bottom，清单恰好占据剩余空间。
        Controls.Add(_targetGrid);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);
    }

    public string BackupStatusText => _backupLabel.Text;

    public bool ConfirmButtonEnabled => _confirmButton.Enabled;

    /// <summary>
    /// N-07（R4 实证）：CenterParent 已设，但实测 .NET 10 WinForms 对非模态 <c>Show(owner)</c> 不按 owner 居中
    /// （ShowDialog 才生效），故加载时按 owner 矩形显式居中；owner 部分出屏时钳制到所在屏工作区。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Owner is not { } owner)
        {
            return;
        }

        var location = new Point(
            owner.Left + (owner.Width - Width) / 2,
            owner.Top + (owner.Height - Height) / 2);
        var workingArea = Screen.FromControl(this).WorkingArea;
        location.X = Math.Max(workingArea.Left, Math.Min(location.X, workingArea.Right - Width));
        location.Y = Math.Max(workingArea.Top, Math.Min(location.Y, workingArea.Bottom - Height));
        Location = location;
    }

    /// <summary>N-07 冒烟：清单控件在客户区内的矩形（验证随窗伸缩）。</summary>
    public Rectangle ListBounds => _targetGrid.Bounds;

    /// <summary>N-07 冒烟：两个操作按钮是否完整位于客户区内（缩放/最大化不遮挡）。</summary>
    public bool ActionButtonsFullyVisible()
    {
        var clientScreen = RectangleToScreen(new Rectangle(Point.Empty, ClientSize));
        return new Control[] { _confirmButton, _cancelButton }.All(b =>
            b.Visible && b.Width > 0 && b.Height > 0
            && clientScreen.Contains(b.RectangleToScreen(b.ClientRectangle)));
    }

    public bool RunFinished { get; private set; }

    public CleanupExecutionResult? LastResult { get; private set; }

    /// <summary>本对话框使用的计划与备份事实（T-31 结果报告构建源）。</summary>
    public CleanupPlan Plan => _plan;

    public VerifiedBackup? Backup => _backupFacts?.Verified;

    public string ZipPath => _backupFacts?.ZipPath ?? string.Empty;

    // 冒烟/自动化入口：等价于点击对应按钮。
    public void RequestConfirm() => OnConfirmClick(this, EventArgs.Empty);

    public void RequestCancel() => OnCancelClick(this, EventArgs.Empty);

    /// <summary>删除进入终态后触发（携带逐项结果，供主窗刷新清单）。</summary>
    public event EventHandler<CleanupExecutionResult>? CleanupFinished;

    private async void OnConfirmClick(object? sender, EventArgs e)
    {
        // N-04/N-05：未携带备份事实时 manifest/backup 为 null（未备份直接确认）；携带时 executor 仍逐项校验。
        if (_running)
        {
            return;
        }

        _running = true;
        _confirmButton.Enabled = false;
        _cancelButton.Text = "取消执行(&C)";
        _resultLabel.Text = "正在删除…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await _presenter.RunCleanupAsync(
                _plan, _backupFacts?.Manifest, _backupFacts?.Verified, _root, userConfirmed: true, cts.Token);
            LastResult = result;
            _resultLabel.Text = result.Aborted
                ? $"已中止：{result.AbortCode}（未执行任何删除）"
                : $"删除 {result.DeletedCount} 个 / 跳过 {result.SkippedCount} 个 / 失败 {result.FailedCount} 个，" +
                  $"共释放 {InventoryPresenter.FormatBytes(result.DeletedBytes)}。";
        }
        catch (Exception ex)
        {
            _resultLabel.Text = "删除异常：" + ex.Message;
        }

        _running = false;
        RunFinished = true;
        _cancelButton.Text = "关闭(&L)";
        if (LastResult is not null)
        {
            CleanupFinished?.Invoke(this, LastResult);
        }
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            _cancelButton.Enabled = false;
            _resultLabel.Text = "正在取消（剩余目标将逐项跳过）…";
            return;
        }

        Close(); // 未开始执行：直接关闭，不产生任何删除。
    }
}
