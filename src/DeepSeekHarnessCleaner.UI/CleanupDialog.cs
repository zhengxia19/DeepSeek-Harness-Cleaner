using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 删除确认对话框（T-30）：展示最终删除清单（逐文件路径/大小）、排除项与进程警告、备份状态；
/// “确认删除”在备份完成前禁用；确认后后台执行删除（可取消），完成触发 <see cref="CleanupFinished"/>。
/// 取消（未开始执行时）直接关闭，不产生任何删除。
/// </summary>
public sealed class CleanupDialog : Form
{
    private readonly ListView _targetList = new();
    private readonly Label _planLabel = new();
    private readonly Label _warningLabel = new();
    private readonly Label _backupLabel = new();
    private readonly Label _resultLabel = new();
    private readonly Button _backupButton = new();
    private readonly Button _confirmButton = new();
    private readonly Button _cancelButton = new();
    private readonly ActionsPresenter _presenter;
    private readonly CleanupPlan _plan;
    private readonly IReadOnlyList<PlanExclusion> _exclusions;
    private readonly string _root;
    private readonly string _dataHome;
    private readonly IReadOnlyList<SessionSummary> _sessions;
    private string _zipPath;
    private CancellationTokenSource? _cts;
    private BackupManifest? _manifest;
    private VerifiedBackup? _backup;
    private bool _running;

    public CleanupDialog(
        CleanupPlanResult planResult,
        string root,
        string dataHome,
        IReadOnlyList<SessionSummary> sessions,
        ActionsPresenter presenter,
        string zipPath)
    {
        _plan = planResult.Plan!;
        _exclusions = planResult.Exclusions;
        _root = root;
        _dataHome = dataHome;
        _sessions = sessions;
        _presenter = presenter;
        _zipPath = zipPath;

        Text = "删除确认：会话原始文件";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(640, 420);

        _planLabel.AutoSize = true;
        _planLabel.Location = new Point(12, 10);
        _planLabel.Text = $"会话 {sessions.Count} 个；待删除文件 {_plan.Targets.Count} 个" +
            $"（合计 {InventoryPresenter.FormatBytes(_plan.Targets.Sum(static t => t.ExpectedLength))}）" +
            (_exclusions.Count > 0 ? $"；排除项 {_exclusions.Count} 个" : string.Empty);

        var probe = presenter.ProbeProcesses();
        _warningLabel.AutoSize = true;
        _warningLabel.Location = new Point(12, 34);
        _warningLabel.ForeColor = probe.AllowsWrite ? Color.FromKnownColor(KnownColor.ControlText) : Color.Firebrick;
        _warningLabel.Text = probe.AllowsWrite
            ? "进程状态：Harness 未运行，可执行删除。"
            : $"警告：进程状态 {probe.State}（{(probe.ErrorCode ?? string.Join("；", probe.RiskNotes))}），删除将被阻断。";

        _backupLabel.AutoSize = true;
        _backupLabel.Location = new Point(12, 58);
        _backupLabel.Text = "备份状态：未备份（删除前必须先完成原始文件 ZIP 备份）。";

        _targetList.Location = new Point(12, 84);
        _targetList.Size = new Size(616, 250);
        _targetList.View = View.Details;
        _targetList.FullRowSelect = true;
        _targetList.HideSelection = true;
        _targetList.Columns.Add("文件（相对数据根）", 470);
        _targetList.Columns.Add("大小", 80, HorizontalAlignment.Right);
        _targetList.Columns.Add("类别", 60);
        foreach (var target in _plan.Targets)
        {
            var relative = Path.GetRelativePath(_root, target.AbsolutePath);
            _targetList.Items.Add(new ListViewItem(new[]
            {
                relative,
                InventoryPresenter.FormatBytes(target.ExpectedLength),
                target.Category.ToString(),
            }));
        }

        _backupButton.Text = "选择目录并备份(&B)";
        _backupButton.AutoSize = true;
        _backupButton.Location = new Point(12, 344);
        _backupButton.TabIndex = 20;
        _backupButton.Click += OnBackupClick;

        _confirmButton.Text = "确认删除(&D)";
        _confirmButton.AutoSize = true;
        _confirmButton.Location = new Point(240, 344);
        _confirmButton.TabIndex = 21;
        _confirmButton.Enabled = false; // 未备份不得删除。
        _confirmButton.Click += OnConfirmClick;

        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.Location = new Point(480, 344);
        _cancelButton.TabIndex = 22;
        _cancelButton.Click += OnCancelClick;

        _resultLabel.AutoSize = true;
        _resultLabel.Location = new Point(12, 384);
        _resultLabel.Text = string.Empty;

        Controls.AddRange(new Control[]
        {
            _planLabel, _warningLabel, _backupLabel, _targetList,
            _backupButton, _confirmButton, _cancelButton, _resultLabel,
        });
    }

    public bool BackupReady => _backup is not null;

    public string BackupStatusText => _backupLabel.Text;

    public bool ConfirmButtonEnabled => _confirmButton.Enabled;

    public bool RunFinished { get; private set; }

    public CleanupExecutionResult? LastResult { get; private set; }

    /// <summary>本对话框使用的计划与备份事实（T-31 结果报告构建源）。</summary>
    public CleanupPlan Plan => _plan;

    public VerifiedBackup? Backup => _backup;

    public string ZipPath => _zipPath;

    // 冒烟/自动化入口：等价于点击对应按钮。
    public void RequestBackup() => OnBackupClick(this, EventArgs.Empty);

    public void RequestConfirm() => OnConfirmClick(this, EventArgs.Empty);

    public void RequestCancel() => OnCancelClick(this, EventArgs.Empty);

    /// <summary>删除进入终态后触发（携带逐项结果，供主窗刷新清单）。</summary>
    public event EventHandler<CleanupExecutionResult>? CleanupFinished;

    private void OnBackupClick(object? sender, EventArgs e)
    {
        // UI 点击选择目标文件夹；冒烟入口 RequestBackup 保持无交互，避免破坏自动化。
        if (ReferenceEquals(sender, _backupButton))
        {
            using var folderPicker = new FolderBrowserDialog
            {
                Description = "选择会话 ZIP 备份保存目录",
                SelectedPath = Path.GetDirectoryName(_zipPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ShowNewFolderButton = true,
            };
            if (folderPicker.ShowDialog(this) != DialogResult.OK) return;
            _zipPath = Path.Combine(folderPicker.SelectedPath, Path.GetFileName(_zipPath));
        }
        // 清单/备份以数据根（.dsh 主目录）为根：条目名须为 sessions/… 类别前缀。
        var manifestResult = _presenter.BuildSessionManifest(_dataHome, _sessions);
        if (manifestResult.Manifest is null)
        {
            _backupLabel.Text = "清单构建失败：" + string.Join("；", manifestResult.Issues.Select(static i => $"{i.Code}:{i.Path}"));
            return;
        }

        _manifest = manifestResult.Manifest;
        _backupButton.Enabled = false;
        _backupLabel.Text = "备份状态：正在备份…";
        var dialog = new BackupDialog(_manifest, _zipPath, _presenter);
        dialog.Completed += (_, _) =>
        {
            if (dialog.State.Stage == BackupStage.Ready)
            {
                _backup = dialog.State.ZipPath is null ? null : ReopenVerifiedBackup();
                _backupLabel.Text = $"备份状态：已备份并校验（{dialog.State.EntryCount} 个文件，" +
                    $"{InventoryPresenter.FormatBytes(dialog.State.TotalBytes)}）。可确认删除。";
                _confirmButton.Enabled = true;
            }
            else
            {
                _backupLabel.Text = $"备份状态：{dialog.State.Stage}（{dialog.State.ErrorCode} {dialog.State.Message}）。";
                _backupButton.Enabled = true;
            }
        };
        dialog.Show(this);
    }

    private VerifiedBackup? ReopenVerifiedBackup()
    {
        var verified = new BackupService().VerifyZip(_zipPath, _manifest!);
        return verified.Success ? verified.Backup : null;
    }

    private async void OnConfirmClick(object? sender, EventArgs e)
    {
        if (_manifest is null || _backup is null)
        {
            return;
        }

        _running = true;
        _backupButton.Enabled = false;
        _confirmButton.Enabled = false;
        _cancelButton.Text = "取消执行(&C)";
        _resultLabel.Text = "正在删除…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await _presenter.RunCleanupAsync(_plan, _manifest, _backup, _root, userConfirmed: true, cts.Token);
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
