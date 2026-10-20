using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>附件独立盘点与显式选择入口。未勾选文件不加入计划，复用原始 ZIP 和安全删除门禁。</summary>
public sealed class AttachmentsForm : Form
{
    private readonly ListView _files = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        Dock = DockStyle.Fill,
        ShowItemToolTips = true
    };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
    private readonly Button _refresh = new() { Text = "刷新(&R)", AutoSize = true };
    private readonly Button _delete = new() { Text = "删除勾选附件(&D)", AutoSize = true, Enabled = false };
    private readonly string _dataHome;
    private readonly IReadOnlyList<SessionSummary> _sessions;
    private readonly ActionsPresenter _actions;
    private readonly CancellationTokenSource _cts = new();
    private CleanupDialog? _cleanup;
    private bool _loading;

    public AttachmentsForm(string dataHome, IReadOnlyList<SessionSummary> sessions, ActionsPresenter actions)
    {
        _dataHome = dataHome;
        _sessions = sessions;
        _actions = actions;
        Text = "附件管理";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 620);
        MinimumSize = new Size(650, 420);
        var warning = new Label
        {
            Text = "附件不会随会话自动删除。Unknown 表示引用不可判定，不表示孤儿；仅处理人工勾选的普通文件，备份可选。名称和类型仅展示可验证信息。",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(8),
            MaximumSize = new Size(900, 0)
        };
        _files.Columns.Add("名称", 220);
        _files.Columns.Add("类型（会话标注）", 170);
        _files.Columns.Add("关联会话", 250);
        _files.Columns.Add("大小", 110);
        _files.Columns.Add("引用状态", 200);
        _files.Columns.Add("存储路径（相对 attachments）", 420);
        _files.Columns.Add("附件 ID", 430);
        var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(8) };
        controls.Controls.AddRange([_refresh, _delete]);
        Controls.Add(_files);
        Controls.Add(_status);
        Controls.Add(warning);
        Controls.Add(controls);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _delete.Click += OnDelete;
        _files.ItemChecked += (_, _) => { if (!_loading) _delete.Enabled = _files.CheckedItems.Count > 0 && _cleanup is not { IsDisposed: false }; };
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    protected override void OnShown(EventArgs e)
    {
        OwnedFormPosition.Center(this);
        base.OnShown(e);
    }

    private async Task RefreshAsync()
    {
        if (_loading || IsDisposed) return;
        _loading = true;
        _refresh.Enabled = false;
        _delete.Enabled = false;
        _status.Text = "正在只读扫描附件及引用…";
        try
        {
            var result = await Task.Run(() => new AttachmentInventoryService().Scan(_dataHome, _sessions, _cts.Token), _cts.Token);
            if (IsDisposed) return;
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (var file in result.Items)
            {
                var status = file.Candidate.EvidenceStatus == AttachmentEvidenceStatus.Referenced
                    ? $"已引用（{file.ReferencingSessionIds.Count} 个会话）" : "Unknown（引用不可判定）";
                var related = file.ReferencingSessionIds.Select(id =>
                {
                    var title = _sessions.FirstOrDefault(s => s.SessionId == id)?.Title;
                    return string.IsNullOrWhiteSpace(title) ? id : $"{title}（{id}）";
                });
                _files.Items.Add(new ListViewItem([file.DisplayName, file.TypeLabel, string.Join("；", related),
                    InventoryPresenter.FormatBytes(file.Candidate.ExpectedLength), status, file.RelativePath,
                    file.AttachmentId ?? "（映射未验证）"])
                { Tag = file, ToolTipText = string.Join(Environment.NewLine, file.ReferencingSessionIds) });
            }
            _files.EndUpdate();
            _status.Text = $"附件文件 {result.Items.Count} 个；合计 {InventoryPresenter.FormatBytes(result.Items.Sum(x => x.Candidate.ExpectedLength))}；"
                + (result.Issues.Count == 0 ? "无扫描异常。" : $"有 {result.Issues.Count} 项读取/路径限制，引用覆盖不完整。")
                + " 未引用文件统一保留 Unknown，不作孤儿判断。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "附件扫描失败：" + ex.Message; }
        finally
        {
            _loading = false;
            if (!IsDisposed) _refresh.Enabled = true;
        }
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        if (_loading || _cleanup is { IsDisposed: false }) return;
        var selected = _files.CheckedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<AttachmentInventoryItem>().ToArray();
        if (selected.Length == 0) return;
        var paths = selected.Select(x => x.Candidate.FullPath).ToArray();
        var selection = new AttachmentCleanupSelection().Select(_dataHome, selected.Select(x => x.Candidate).ToArray(), paths);
        if (selection.Plan is null || selection.Plan.Targets.Count == 0) { _status.Text = "无有效附件目标，请重新扫描。"; return; }
        var plan = new CleanupPlanResult(selection.RootErrorCode, selection.Plan,
            selection.Exclusions.Select(x => new PlanExclusion(null, x.Reason, x.Detail)).ToArray());
        var zip = Path.Combine(Path.GetTempPath(), "dshcleaner-backups", $"attachments-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var dialog = new CleanupDialog(plan, _dataHome, _dataHome, [], _actions, zip, paths);
        _cleanup = dialog;
        _delete.Enabled = false;
        _refresh.Enabled = false;
        dialog.CleanupFinished += (_, result) =>
        {
            if (IsDisposed) return;
            var report = _actions.BuildReport(dialog.Plan, dialog.Backup, result, dialog.ZipPath);
            new ResultsDialog(report).Show(this);
            _ = RefreshAsync();
        };
        dialog.FormClosed += (_, _) =>
        {
            _cleanup = null;
            if (!IsDisposed) { _refresh.Enabled = true; _delete.Enabled = _files.CheckedItems.Count > 0; }
        };
        dialog.Show(this);
    }
}
