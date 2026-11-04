using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>Explicit, independent selection of data and desktop roots. Discovery is read-only.</summary>
public sealed class RootSelectionDialog : Form
{
    private readonly RootLocator _locator;
    private readonly string? _savedDsh;
    private readonly string? _savedDesktop;
    private string? _manualDsh;
    private string? _manualDesktop;
    private readonly ListBox _dshCandidates = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ListBox _desktopCandidates = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly CheckBox _desktopConfirmed = new() { Text = "确认使用选中的桌面根（独立于会话根）", AutoSize = true };
    private readonly CheckBox _remember = new() { Text = "记住所选根路径", AutoSize = true };
    private readonly Button _ok = new() { Text = "使用所选根", AutoSize = true };

    public RootSelectionDialog(RootLocator locator, AppSettings settings, string? currentDsh = null, string? currentDesktop = null)
    {
        _locator = locator;
        _savedDsh = settings.RememberRoots && settings.SessionRoot is { Length: > 0 } session
            && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(session)), "sessions", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(session)) : null;
        _savedDesktop = settings.RememberRoots ? settings.DesktopRoot : null;
        _manualDsh = currentDsh;
        _manualDesktop = currentDesktop;
        Text = "选择数据根";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 540);
        MinimumSize = new Size(640, 400);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Header("会话数据根：多个有效候选时必须明确选择", () => Browse(false)), 0, 0);
        layout.Controls.Add(_dshCandidates, 0, 1);
        layout.Controls.Add(Header("桌面根：可不选择；不会自动跟随会话根", () => Browse(true)), 0, 2);
        layout.Controls.Add(_desktopCandidates, 0, 3);
        layout.Controls.Add(_desktopConfirmed, 0, 4);
        _remember.Checked = settings.RememberRoots;
        layout.Controls.Add(_remember, 0, 5);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;
        _dshCandidates.SelectedIndexChanged += (_, _) => UpdateConfirmation();
        _desktopCandidates.SelectedIndexChanged += (_, _) => { _desktopConfirmed.Checked = false; UpdateConfirmation(); };
        _desktopConfirmed.CheckedChanged += (_, _) => UpdateConfirmation();
        _ok.Click += (_, _) => { if (_ok.Enabled) { DialogResult = DialogResult.OK; Close(); } };
        RefreshCandidates();
        if (currentDesktop is not null && Selected(_desktopCandidates) is { } desktop && Same(desktop.NormalizedPath, currentDesktop))
            _desktopConfirmed.Checked = true;
    }

    public string? SelectedDshHome => Selected(_dshCandidates)?.NormalizedPath;
    public string? SelectedDesktopHome => _desktopConfirmed.Checked ? Selected(_desktopCandidates)?.NormalizedPath : null;
    public bool RememberRoots => _remember.Checked;

    private static FlowLayoutPanel Header(string text, Action browse)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        var button = new Button { Text = "手选目录…", AutoSize = true };
        button.Click += (_, _) => browse();
        panel.Controls.Add(button);
        return panel;
    }

    private void Browse(bool desktop)
    {
        using var picker = new FolderBrowserDialog { Description = desktop ? "选择桌面数据根" : "选择 .dsh 数据根（包含 sessions/storages/attachments）", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (desktop) _manualDesktop = picker.SelectedPath;
        else _manualDsh = picker.SelectedPath;
        RefreshCandidates();
    }

    private void RefreshCandidates()
    {
        var result = _locator.DiscoverCandidates(_manualDsh, _savedDsh, _manualDesktop, _savedDesktop);
        Fill(_dshCandidates, result.Candidates.Where(x => x.Kind == RootCandidateKind.DshHome), _manualDsh, true);
        Fill(_desktopCandidates, result.Candidates.Where(x => x.Kind == RootCandidateKind.DesktopHome), _manualDesktop, false);
        _desktopConfirmed.Checked = false;
        UpdateConfirmation();
    }

    private static void Fill(ListBox list, IEnumerable<RootCandidate> candidates, string? current, bool allowSingle)
    {
        list.Items.Clear();
        foreach (var candidate in candidates) list.Items.Add(new CandidateItem(candidate));
        var valid = list.Items.Cast<CandidateItem>().Where(x => x.Candidate.Status == RootCandidateStatus.Valid).ToArray();
        var chosen = valid.FirstOrDefault(x => current is not null && Same(x.Candidate.NormalizedPath, current));
        if (chosen is null && allowSingle && valid.Length == 1) chosen = valid[0];
        list.SelectedItem = chosen;
    }

    private static bool Same(string? left, string right) => string.Equals(left, Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static RootCandidate? Selected(ListBox list) => list.SelectedItem is CandidateItem { Candidate.Status: RootCandidateStatus.Valid } item ? item.Candidate : null;
    private void UpdateConfirmation()
    {
        _desktopConfirmed.Enabled = Selected(_desktopCandidates) is not null;
        _ok.Enabled = SelectedDshHome is not null && (!_desktopConfirmed.Checked || Selected(_desktopCandidates) is not null);
    }

    private sealed record CandidateItem(RootCandidate Candidate)
    {
        public override string ToString() => $"{Candidate.NormalizedPath ?? Candidate.InputPath} | {string.Join("/", Candidate.Sources.Select(SourceText))} | {Candidate.Status switch
        {
            RootCandidateStatus.Valid => "有效",
            RootCandidateStatus.Missing => "不存在",
            RootCandidateStatus.Inaccessible => "无法访问",
            _ => "无效",
        }} {Candidate.Reason}";

        private static string SourceText(RootCandidateSource source) => source switch
        {
            RootCandidateSource.Default => "账户默认",
            RootCandidateSource.EnvironmentVariable => "DSH_HOME 环境变量",
            RootCandidateSource.Saved => "已记忆",
            _ => "手选",
        };
    }
}
