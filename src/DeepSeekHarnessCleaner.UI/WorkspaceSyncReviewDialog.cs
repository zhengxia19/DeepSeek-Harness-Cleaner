using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>Explicit review/recovery/retry for one known cleanup context; never executes physical deletion.</summary>
public sealed class WorkspaceSyncReviewDialog : Form
{
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Dock = DockStyle.Fill };
    private readonly CheckBox _approveRecovery = new() { Text = "已核对状态，仅恢复缺失元数据或确认已应用；不恢复会话日志", AutoSize = true };
    private readonly Button _inspect = new() { Text = "只读复查", AutoSize = true };
    private readonly Button _resolve = new() { Text = "恢复 / 确认结果", AutoSize = true };
    private readonly Button _retry = new() { Text = "重新复查并重试同步", AutoSize = true };
    private readonly WorkspaceSyncCoordinator _coordinator;
    private readonly WorkspaceRecoveryService _recovery;
    private readonly CleanupPlan _cleanup;
    private readonly PreCleanupAudit _before;
    private readonly CleanupExecutionResult _execution;
    private WorkspaceSyncAttempt _attempt;
    private WorkspaceRecoveryResult? _inspection;
    private bool _busy;
    private string _notice = "先只读复查。若有中断，确认恢复结果后才可重试；不会重新删除文件。";

    public WorkspaceSyncReviewDialog(WorkspaceSyncCoordinator coordinator, WorkspaceRecoveryService recovery,
        CleanupPlan cleanup, PreCleanupAudit before, CleanupExecutionResult execution, WorkspaceSyncAttempt attempt)
    {
        (_coordinator, _recovery, _cleanup, _before, _execution, _attempt) = (coordinator, recovery, cleanup, before, execution, attempt);
        Text = "Workspace 同步复查与恢复";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 440);
        MinimumSize = new Size(640, 380);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([_inspect, _resolve, _retry]);
        layout.Controls.Add(_details, 0, 0);
        layout.Controls.Add(_approveRecovery, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        _approveRecovery.CheckedChanged += (_, _) => Render();
        _inspect.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            _approveRecovery.Checked = false;
            _inspection = await Task.Run(() => _recovery.Inspect(_attempt.Plan!, _attempt.Backup!));
            _notice = Describe(_inspection);
        });
        _resolve.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            var result = await Task.Run(() => _recovery.Resolve(_attempt.Plan!, _attempt.Backup!));
            _inspection = result;
            _notice = Describe(result);
            _approveRecovery.Checked = false;
        });
        _retry.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            _attempt = await Task.Run(() => _coordinator.Retry(_cleanup, _before, _execution, _attempt));
            _inspection = null;
            _notice = "重试已结束：" + _attempt.Outcome.State + "。文件删除结果未改动。";
            AttemptChanged?.Invoke(this, _attempt);
        });
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        Render();
    }

    public event EventHandler<WorkspaceSyncAttempt>? AttemptChanged;

    protected override void OnShown(EventArgs e) { OwnedFormPosition.Center(this); base.OnShown(e); }

    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        Render();
        try { await operation(); }
        catch (Exception) { _inspection = null; _notice = "复查或操作失败；结果未知，请保留已有材料并重新复查。"; }
        finally { _busy = false; if (!IsDisposed) Render(); }
    }

    private void Render()
    {
        var bound = _attempt.Outcome.CleanupPlanId == _cleanup.PlanId && _before.Matches(_cleanup);
        var recoverable = _attempt.Outcome.State == WorkspaceSyncState.RecoveryRequired && _attempt.Plan is not null && _attempt.Backup is not null;
        _inspect.Enabled = !_busy && bound && recoverable;
        _approveRecovery.Enabled = !_busy && recoverable && _inspection?.State is WorkspaceRecoveryState.OriginalUnchanged or WorkspaceRecoveryState.OriginalRetained or WorkspaceRecoveryState.AppliedVerified;
        _resolve.Enabled = _approveRecovery.Enabled && _approveRecovery.Checked;
        _retry.Enabled = !_busy && bound && (_attempt.Outcome.State == WorkspaceSyncState.Blocked || recoverable && _inspection?.Resolved == true);
        _details.Text = "原数据根：" + _before.Snapshot.DataHome + Environment.NewLine
            + "本次文件执行：删除 " + _execution.DeletedCount + " / 跳过 " + _execution.SkippedCount + " / 失败 " + _execution.FailedCount + Environment.NewLine
            + "元数据结果：" + _attempt.Outcome.State + "；" + (_attempt.Outcome.ErrorCode ?? "无错误码") + Environment.NewLine
            + "原始备份：" + (_attempt.Outcome.BackupDirectory ?? "尚无已验证备份") + Environment.NewLine + Environment.NewLine
            + (_busy ? "正在复验实际文件，请稍候…" : _notice)
            + Environment.NewLine + "恢复与同步不是跨文件原子事务；遇到新文件、变化或未知状态将停止。";
    }

    private static string Describe(WorkspaceRecoveryResult result) => result.State switch
    {
        WorkspaceRecoveryState.OriginalUnchanged => "原元数据未变；可核对后保留候选并结束本次中断。",
        WorkspaceRecoveryState.OriginalRetained => "源元数据缺失，已核实原文件和备份；确认后仅恢复该原元数据。",
        WorkspaceRecoveryState.AppliedVerified => "已核实本次元数据变更；确认后保留结果并结束中断。",
        WorkspaceRecoveryState.ResolvedOriginal => "原元数据已确认/恢复，材料已保留；可以重新复查并重试。",
        WorkspaceRecoveryState.ResolvedApplied => "已确认本次元数据结果；重试只复查剩余引用，不重复修改。",
        _ => "无法验证恢复条件，未确认恢复完成。保留材料；原因：" + result.ErrorCode,
    };
}
