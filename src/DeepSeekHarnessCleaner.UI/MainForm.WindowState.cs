using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

public sealed partial class MainForm
{
    private readonly WindowStateStore? _windowStore;
    private readonly Action<string> _windowNotice;
    private string? _windowLoadError;
    private WindowStateRecord? _closingWindowState;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_windowStore is null) return;
        var loaded = _windowStore.Load();
        _windowLoadError = loaded.Error;
        if (loaded.State is not { } state) return;
        StartPosition = FormStartPosition.Manual;
        var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
        Bounds = WindowStateStore.Fit(state, areas, MinimumSize, DeviceDpi);
        if (state.WindowState == "Maximized") WindowState = FormWindowState.Maximized;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_windowLoadError is not null) _windowNotice(_windowLoadError);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || _windowStore is null) { _closingWindowState = null; return; }
        var rectangle = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _closingWindowState = new WindowStateRecord(1, new WindowRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
            WindowState == FormWindowState.Maximized ? "Maximized" : "Normal", DeviceDpi);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_windowStore is not null && _closingWindowState is not null)
        {
            var error = _windowStore.Save(_closingWindowState);
            if (error is not null) _windowNotice(error);
        }
        base.OnFormClosed(e);
    }
}
