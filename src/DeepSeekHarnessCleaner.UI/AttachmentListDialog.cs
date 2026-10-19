using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// N-06 附件列表对话框（只读展示，无删除操作）：打开后经 <see cref="AttachmentListPresenter"/>
/// 后台构建附件索引，可取消。列 = 附件 ID、大小、引用状态、引用会话数；
/// 选中行在下方展开引用会话 ID；底部标注悬空引用与不可判定（GlobalOrphanConclusionAvailable=false）。
/// </summary>
public sealed class AttachmentListDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _detailBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _footerLabel = new();
    private readonly Button _cancelButton = new();
    private readonly Button _closeButton = new();
    private readonly string _dataHome;
    private readonly IReadOnlyList<SessionSummary> _sessions;
    private readonly CancellationTokenSource _cts = new();
    private AttachmentListOutcome? _outcome;

    public AttachmentListDialog(string dataHome, IReadOnlyList<SessionSummary> sessions)
    {
        _dataHome = dataHome;
        _sessions = sessions;

        Text = "附件列表（只读）";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MinimumSize = new Size(640, 420);
        ClientSize = new Size(760, 450);

        _statusLabel.AutoSize = true;
        _statusLabel.Location = new Point(12, 12);
        _statusLabel.Text = "正在后台构建附件索引…";
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        _grid.Location = new Point(12, 36);
        _grid.Size = new Size(736, 240);
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "attId", HeaderText = "附件 ID", FillWeight = 300 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小（字节）", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "引用状态", FillWeight = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "refs", HeaderText = "引用会话数", FillWeight = 90 });
        _grid.SelectionChanged += OnGridSelectionChanged;

        var detailLabel = new Label
        {
            AutoSize = true,
            Location = new Point(12, 284),
            Text = "引用会话 ID（选中行展开）：",
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };

        _detailBox.Location = new Point(12, 302);
        _detailBox.Size = new Size(736, 60);
        _detailBox.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _detailBox.Multiline = true;
        _detailBox.ReadOnly = true;
        _detailBox.ScrollBars = ScrollBars.Vertical;

        _footerLabel.AutoSize = true;
        _footerLabel.MaximumSize = new Size(736, 0);
        _footerLabel.Location = new Point(12, 370);
        _footerLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _footerLabel.Text = string.Empty;

        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.Location = new Point(540, 415);
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancelButton.Click += OnCancelClick;

        _closeButton.Text = "关闭(&L)";
        _closeButton.AutoSize = true;
        _closeButton.Location = new Point(650, 415);
        _closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _closeButton.Enabled = false;
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            _statusLabel, _grid, detailLabel, _detailBox, _footerLabel, _cancelButton, _closeButton,
        });
        Shown += (_, _) => _ = RunAsync();
    }

    /// <summary>索引构建进入终态（完成/取消）后为 true。</summary>
    public bool BuildFinished { get; private set; }

    public AttachmentListOutcome? Outcome => _outcome;

    public string StatusText => _statusLabel.Text;

    public string FooterText => _footerLabel.Text;

    public string DetailText => _detailBox.Text;

    /// <summary>当前列表行的快照（附件 ID、大小、引用状态文本、引用会话数），供一致性断言。</summary>
    public IReadOnlyList<(string Id, long Size, string Status, int Refs)> GridSnapshot()
    {
        var rows = new List<(string, long, string, int)>(_grid.Rows.Count);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            rows.Add((
                Convert.ToString(row.Cells["attId"].Value) ?? string.Empty,
                Convert.ToInt64(row.Cells["size"].Value),
                Convert.ToString(row.Cells["status"].Value) ?? string.Empty,
                Convert.ToInt32(row.Cells["refs"].Value)));
        }

        return rows;
    }

    /// <summary>按附件 ID 选中行（冒烟用）；未找到返回 false。</summary>
    public bool SelectRowByAttachmentId(string attachmentId)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (string.Equals(Convert.ToString(row.Cells["attId"].Value), attachmentId, StringComparison.Ordinal))
            {
                // 设 CurrentCell 才会同步 CurrentRow 并触发 SelectionChanged；仅 Selected=true 不够。
                _grid.CurrentCell = row.Cells["attId"];
                row.Selected = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>引用状态 → 展示文本（冒烟断言共用映射）。</summary>
    internal static string StatusDisplay(AttachmentEvidenceStatus status) => status switch
    {
        AttachmentEvidenceStatus.Referenced => "被引用",
        AttachmentEvidenceStatus.ConfirmedUnreferenced => "无引用（确认）",
        _ => "不可判定",
    };

    private async Task RunAsync()
    {
        try
        {
            var outcome = await new AttachmentListPresenter().BuildAsync(_dataHome, _sessions, _cts.Token);
            if (IsDisposed)
            {
                return;
            }

            _outcome = outcome;
            foreach (var row in outcome.Rows)
            {
                _grid.Rows.Add(
                    row.AttachmentId,
                    row.SizeBytes,
                    StatusDisplay(row.Status),
                    row.ReferencingSessionIds.Count);
            }

            _statusLabel.Text = $"附件索引完成：附件 {outcome.Rows.Count} 个；" +
                $"悬空引用 {outcome.DanglingReferences.Count} 个；" +
                $"未解析会话 {outcome.UnresolvedSessionIds.Count} 个。";
            _footerLabel.Text = BuildFooter(outcome);
            _detailBox.Text = "（未选中行）";
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = "附件索引构建已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = "附件索引构建失败：" + ex.Message;
            }
        }

        BuildFinished = true;
        _cancelButton.Enabled = false;
        _closeButton.Enabled = true;
    }

    private static string BuildFooter(AttachmentListOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.AttachmentsRootError is not null)
        {
            parts.Add("数据根下未找到 attachments 目录（附件列表为空）。");
        }

        if (outcome.DanglingReferences.Count > 0)
        {
            var ids = outcome.DanglingReferences.Select(static d => d.AttachmentId).Take(5);
            var suffix = outcome.DanglingReferences.Count > 5 ? "…" : string.Empty;
            parts.Add($"悬空引用 {outcome.DanglingReferences.Count} 个（{string.Join("、", ids)}{suffix}）。");
        }
        else
        {
            parts.Add("无悬空引用。");
        }

        if (!outcome.GlobalOrphanConclusionAvailable)
        {
            parts.Add($"不可判定：存在未解析会话（{outcome.UnresolvedSessionIds.Count} 个），" +
                "全局孤儿结论不可得（GlobalOrphanConclusionAvailable=false），未引用附件按“不可判定”展示。");
        }

        return string.Join(" ", parts);
    }

    private void OnGridSelectionChanged(object? sender, EventArgs e)
    {
        var row = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : _grid.CurrentRow;
        if (row is null)
        {
            return;
        }

        var id = Convert.ToString(row.Cells["attId"].Value) ?? string.Empty;
        var refs = _outcome?.Rows.FirstOrDefault(r => r.AttachmentId == id)?.ReferencingSessionIds;
        if (refs is null || refs.Count == 0)
        {
            _detailBox.Text = "无已验证引用。";
            return;
        }

        _detailBox.Text = $"引用会话 ID（{refs.Count} 个）：" + string.Join("、", refs);
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        if (!BuildFinished)
        {
            _cts.Cancel();
            _statusLabel.Text = "正在取消附件索引构建…";
        }
        else
        {
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnFormClosing(e);
    }
}
