using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.UI;

public sealed partial class MainForm
{
    private void OnSelectionHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.ColumnIndex < 0 || _sessionGrid.Columns[e.ColumnIndex].Name != "checked"
            || _bindingSessions || _scanning || _preparingDelete || _sessionGrid.Rows.Count == 0) return;
        _sessionGrid.EndEdit();
        var rows = _sessionGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is SessionSummary).ToArray();
        var check = !rows.All(r => r.Cells["checked"].Value is true);
        _bindingSessions = true;
        _sessionGrid.SuspendLayout();
        try
        {
            foreach (var row in rows)
            {
                var session = (SessionSummary)row.Tag!;
                row.Cells["checked"].Value = check;
                if (check) _checkedSessions.Add(session); else _checkedSessions.Remove(session);
            }
        }
        finally { _sessionGrid.ResumeLayout(); _bindingSessions = false; }
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        var displayed = _sessionGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<SessionSummary>().ToArray();
        var checkedVisible = displayed.Count(s => _checkedSessions.Contains(s));
        _statusLabel.Text = _presenter.BuildStatusText(displayed) + $"；当前勾选 {checkedVisible}/{displayed.Length}，总勾选 {_checkedSessions.Count}（隐藏 {_checkedSessions.Count - checkedVisible}）";
        _deleteButton.Enabled = !_preparingDelete && (_checkedSessions.Count > 0 || _sessionGrid.SelectedRows.Count > 0);
    }
}
