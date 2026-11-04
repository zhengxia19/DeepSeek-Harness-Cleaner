using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

public sealed partial class MainForm
{
    private readonly RootLocator _rootLocator;
    private readonly SettingsStore _rootSettings;
    private string? _confirmedDshHome;
    private string? _confirmedDesktopHome;
    private bool _choosingRoots;

    private bool HasOpenRootOperations() => _lastWorkspaceReview is { IsDisposed: false, Visible: true }
        || _lastCleanupDialog is { IsDisposed: false, Visible: true }
        || _attachmentsWindow is { IsDisposed: false, Visible: true };

    private void OnChooseRoots(object? sender, EventArgs e)
    {
        if (_scanning || _preparingDelete || _choosingRoots || HasOpenRootOperations())
        {
            _statusLabel.Text = "请先完成操作并关闭删除确认、同步复查或附件窗口，再切换数据根。";
            return;
        }
        try { ChooseRoots(); }
        catch (Exception ex) { _statusLabel.Text = "选择数据根失败：" + ex.Message; }
    }

    private bool ChooseRoots()
    {
        if (_choosingRoots) return false;
        _choosingRoots = true;
        try
        {
            var loaded = _rootSettings.Load();
            using var dialog = new RootSelectionDialog(_rootLocator, loaded.Settings, _confirmedDshHome, _confirmedDesktopHome);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedDshHome is not { } home)
            {
                _rootErrorText = "未确认数据根，未开始扫描。";
                return false;
            }
            var roots = _rootLocator.Resolve(home, dialog.SelectedDesktopHome);
            if (!roots.DshProbe.IsValid || (dialog.SelectedDesktopHome is not null && !roots.DesktopProbe.IsValid))
            {
                InvalidateRootState();
                _rootErrorText = "所选根在确认后已变化或不可访问，请重新选择。";
                return false;
            }
            InvalidateRootState();
            _confirmedDshHome = roots.DshHome;
            _confirmedDesktopHome = dialog.SelectedDesktopHome;
            _statusLabel.Text = $"已选择数据根：{_confirmedDshHome}；桌面根：{_confirmedDesktopHome ?? "未确认"}。请扫描。";
            if (dialog.RememberRoots || loaded.Settings.RememberRoots)
            {
                var saved = _rootSettings.Save(loaded.Settings with
                {
                    RememberRoots = dialog.RememberRoots,
                    SessionRoot = dialog.RememberRoots ? roots.SessionRoot : null,
                    DesktopRoot = dialog.RememberRoots ? _confirmedDesktopHome : null,
                });
                if (saved.Status != SettingsSaveStatus.Saved) _statusLabel.Text += " 路径记忆未保存：" + saved.Status;
            }
            return true;
        }
        finally { _choosingRoots = false; }
    }

    private void InvalidateRootState()
    {
        _previewDebounce.Stop();
        _previewCts?.Cancel();
        _previewCts = null;
        _effectiveScanRoot = null;
        _lastPostCleanupAudit = null;
        _checkedSessions.Clear();
        _presenter.Reset();
        BindSessions([]);
        _deleteButton.Enabled = false;
        _preview.Clear();
        _lastCleanupDialog?.Dispose();
        _lastCleanupDialog = null;
        _lastResultsDialog?.Close();
        _lastResultsDialog = null;
        _lastWorkspaceReview?.Close();
        _lastWorkspaceReview = null;
        _lastWorkspaceSyncAttempt = null;
        _lastWorkspaceSyncOutcome = null;
    }
}
