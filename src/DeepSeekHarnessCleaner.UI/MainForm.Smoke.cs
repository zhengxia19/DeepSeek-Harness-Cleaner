using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-17/T-18 冒烟自检：以 <c>--smoke &lt;结果文件&gt; [--scan-root &lt;目录&gt;]</c> 启动时，窗口显示后自动执行
/// 布局/键盘导航断言；提供 --scan-root 时额外执行数据绑定断言（后台扫描、列表/统计/筛选与服务结果一致、
/// 排序不改变多选指向的会话），写入逐项 PASS/FAIL 结果文件并退出。
/// 字体按 1.25/1.5 倍缩放用于模拟 125%/150% DPI 下的布局（AutoScaleMode.Font 会整体重排）；
/// 干净 VM 上的真实 DPI 实机验证由 T-33 执行。
/// </summary>
public sealed partial class MainForm
{
    private readonly string? _smokeResultPath;
    private readonly List<string> _smokeLines = new();
    private bool _smokeFailed;
    private bool _smokeScanFinished;

    /// <summary>进程退出码：0 表示自检全部通过；1 表示存在 FAIL；2 表示自检异常。</summary>
    public int SmokeExitCode { get; private set; }

    private void OnSmokeShown(object? sender, EventArgs e)
    {
        try
        {
            RunSmokeChecks();
        }
        catch (Exception ex)
        {
            _smokeLines.Add("FAIL 自检异常: " + ex.Message);
            SmokeExitCode = 2;
        }

        if (SmokeExitCode == 0)
        {
            SmokeExitCode = _smokeFailed ? 1 : 0;
        }

        _smokeLines.Add(SmokeExitCode == 0 ? "SMOKE_RESULT=PASS" : "SMOKE_RESULT=FAIL");
        File.WriteAllLines(_smokeResultPath!, _smokeLines);
        Close();
    }

    private void RunSmokeChecks()
    {
        _smokeLines.Add($"INFO 进程 DPI DeviceDpi={DeviceDpi}，字体基准 {_baseFont.Name} {_baseFont.SizeInPoints}pt");

        Check("窗口已显示且句柄已创建", Visible && IsHandleCreated);
        Check("菜单/列表/预览/按钮区/状态栏均可见",
            _menu.Visible && _sessionGrid.Visible && _preview.Visible
            && _actionPanel.Visible && _statusStrip.Visible);
        Check("列表区与预览区并排且不压住按钮区",
            _split.Bottom <= _actionPanel.Top && _split.Top >= _menu.Bottom);

        VerifyActionButtonsFit("100%");
        VerifyKeyboardNavigation();

        if (_scanRootOverride is not null)
        {
            RunBindingChecks(_scanRootOverride);
        }

        foreach (var (label, factor) in new[] { ("125%", 1.25f), ("150%", 1.5f) })
        {
            Font = new Font(_baseFont.Name, _baseFont.SizeInPoints * factor, GraphicsUnit.Point);
            System.Windows.Forms.Application.DoEvents();
            _smokeLines.Add($"INFO 模拟 {label} 缩放后窗体客户区 {ClientSize}");
            VerifyActionButtonsFit(label);
        }
    }

    private void VerifyActionButtonsFit(string scaleLabel)
    {
        foreach (var (name, button) in new[]
                 {
                     ("扫描按钮", _scanButton),
                     ("删除按钮", _deleteButton),
                     ("退出按钮", _exitButton),
                 })
        {
            // 按钮位于 _actionPanel 内，坐标须换算到屏幕空间后再与客户区比较。
            var buttonScreen = button.RectangleToScreen(button.ClientRectangle);
            var clientScreen = RectangleToScreen(ClientRectangle);
            var fits = button.Visible
                && button.Width > 0 && button.Height > 0
                && clientScreen.Contains(buttonScreen);
            Check($"{scaleLabel} {name} 完整位于客户区内", fits);

            if (!fits)
            {
                continue;
            }

            // 命中测试须在按钮的直接父容器内进行：窗体级 GetChildAtPoint 只会返回容器（_actionPanel），
            // 不会返回其子按钮，沿 Parent 链向上也匹配不到。
            var parent = button.Parent!;
            var buttonRect = button.RectangleToScreen(button.ClientRectangle);
            var centerScreen = new Point(buttonRect.Left + buttonRect.Width / 2, buttonRect.Top + buttonRect.Height / 2);
            var topmost = parent.GetChildAtPoint(parent.PointToClient(centerScreen));
            Check($"{scaleLabel} {name} 中心未被遮挡", ReferenceEquals(topmost, button));
        }

        Check($"{scaleLabel} 删除按钮初始禁用（未选择会话）", !_deleteButton.Enabled);
    }

    private void VerifyKeyboardNavigation()
    {
        Check("Tab 顺序：列表 < 预览 < 扫描 < 删除 < 退出",
            _sessionGrid.TabIndex < _preview.TabIndex
            && _preview.TabIndex < _scanButton.TabIndex
            && _scanButton.TabIndex < _deleteButton.TabIndex
            && _deleteButton.TabIndex < _exitButton.TabIndex);

        Check("菜单与按钮含助记键（&）",
            _menu.Items.Cast<ToolStripMenuItem>().All(item => item.Text?.Contains('&', StringComparison.Ordinal) == true)
            && _scanButton.Text.Contains('&', StringComparison.Ordinal)
            && _deleteButton.Text.Contains('&', StringComparison.Ordinal)
            && _exitButton.Text.Contains('&', StringComparison.Ordinal));

        // 删除按钮初始禁用，WinForms 的 Tab 导航会跳过禁用控件：
        // 先验证可达扫描/退出且跳过禁用删除，再临时启用验证可达删除。
        var reachDisabled = TabCycleReachable([_scanButton, _exitButton, _deleteButton]);
        Check("Tab 键导航可达扫描按钮", reachDisabled[0]);
        Check("Tab 键导航可达退出按钮", reachDisabled[1]);
        Check("禁用状态的删除按钮被 Tab 跳过（预期行为）", !reachDisabled[2]);

        _deleteButton.Enabled = true;
        try
        {
            var reachEnabled = TabCycleReachable([_deleteButton]);
            Check("启用后 Tab 键导航可达删除按钮", reachEnabled[0]);
        }
        finally
        {
            _deleteButton.Enabled = false;
        }
    }

    /// <summary>从列表起循环按 Tab，返回每个目标控件是否获得过焦点。</summary>
    private bool[] TabCycleReachable(Control[] targets)
    {
        _sessionGrid.Focus();
        var reached = new bool[targets.Length];
        for (var i = 0; i < 30; i++)
        {
            if (!ProcessTabKey(true))
            {
                break;
            }

            var focused = ActiveControl;
            for (var j = 0; j < targets.Length; j++)
            {
                reached[j] |= ReferenceEquals(focused, targets[j]);
            }

            if (reached.All(r => r))
            {
                break;
            }
        }

        return reached;
    }

    /// <summary>
    /// T-18 数据绑定断言（TC-05/TC-07/TC-08）：在合成 fixture 根上执行扫描，
    /// 逐项核对 DataGridView 列表、状态栏统计、关键字/项目/日期筛选与 InventoryService、
    /// SessionFilter 的服务侧结果一致；验证扫描在后台线程执行且 UI 消息泵持续运行；
    /// 验证列排序后多选仍指向原会话。
    /// </summary>
    private void RunBindingChecks(string fixtureRoot)
    {
        var uiThreadId = Environment.CurrentManagedThreadId;

        // 服务侧期望结果：与 UI 同一套 Application/Infrastructure 契约，独立实例。
        var expectedService = new InventoryService(new UiScanSource());
        var expected = expectedService.Scan(fixtureRoot);
        Check("fixture 服务扫描成功（303 会话 / 1 异常文件）",
            expected.RootErrorCode is null && expected.Sessions.Count == 303 && expected.AnomalousFiles.Count == 1);

        // 触发 UI 扫描（async void + await 续体经消息泵回到 UI 线程），期间持续泵消息证明 UI 不冻结。
        _smokeScanFinished = false;
        _scanButton.PerformClick();
        var pumps = 0;
        var start = Environment.TickCount64;
        while (!_smokeScanFinished && Environment.TickCount64 - start < 15_000)
        {
            System.Windows.Forms.Application.DoEvents();
            pumps++;
        }

        Check("扫描已完成（15 秒内）", _smokeScanFinished);
        _smokeLines.Add($"INFO 扫描等待期间消息泵迭代 {pumps} 次，等待 {Environment.TickCount64 - start} ms");
        Check("扫描在后台线程执行（非 UI 线程）",
            _presenter.ScanSource.LastScanThreadId > 0 && _presenter.ScanSource.LastScanThreadId != uiThreadId);
        Check("扫描期间 UI 消息泵持续运行（未冻结）", pumps >= 2 && Visible && IsHandleCreated);

        Check("列表行数与会话服务结果一致", _sessionGrid.Rows.Count == expected.Sessions.Count);
        Check("列表逐行与会话服务结果一致（ID/大小/状态/Tag）", GridMatches(expected.Sessions));

        var status = _statusLabel.Text ?? string.Empty;
        Check("状态栏总量与服务统计一致（件数/合计/异常文件）",
            status.Contains(expected.Sessions.Count.ToString(), StringComparison.Ordinal)
            && status.Contains(InventoryPresenter.FormatBytes(expected.TotalBytes), StringComparison.Ordinal)
            && status.Contains(expected.AnomalousFiles.Count.ToString(), StringComparison.Ordinal));

        // 关键字筛选（TC-07）。
        var keyword = "smoke-a";
        _keywordBox.Text = keyword;
        _applyFilterButton.PerformClick();
        var expectedKeyword = SessionFilter.Apply(expected.Sessions, new FilterRequest(null, null, null, keyword), _presenter.TimeZone);
        Check("关键字筛选列表与服务筛选一致",
            expectedKeyword.Count == 1 && GridMatches(expectedKeyword)
            && (_statusLabel.Text ?? string.Empty).Contains("筛选后 1 条", StringComparison.Ordinal));

        // 项目筛选（TC-07）。
        _keywordBox.Text = string.Empty;
        _projectBox.Text = "projB";
        _applyFilterButton.PerformClick();
        var expectedProject = SessionFilter.Apply(expected.Sessions, new FilterRequest(null, "projB", null, null), _presenter.TimeZone);
        Check("项目筛选列表与服务筛选一致（多代际归并 1 条）",
            expectedProject.Count == 1 && GridMatches(expectedProject));

        // 日期区间筛选：含当天、未知日期不参与（TC-07）。
        _projectBox.Text = string.Empty;
        var createdAtA = expected.Sessions
            .First(static s => s.SessionId == "smoke-a-0001")
            .CreatedAt!.Value;
        // 与 SessionFilter 相同的时区换算方式（DateTimeOffset 无 ToLocalTime(TimeZoneInfo) 重载）。
        var localA = createdAtA.ToOffset(_presenter.TimeZone.GetUtcOffset(createdAtA));
        var day = DateOnly.FromDateTime(localA.DateTime);
        _dateEnabledCheck.Checked = true;
        _dateFieldCombo.SelectedIndex = 0;
        _dateStartPicker.Value = localA.DateTime.Date;
        _dateEndPicker.Value = _dateStartPicker.Value;
        _applyFilterButton.PerformClick();
        var expectedDate = SessionFilter.Apply(
            expected.Sessions,
            new FilterRequest(new DateFilter(DateFilterField.CreatedAt, day, day), null, null, null),
            _presenter.TimeZone);
        Check("日期区间筛选列表与服务筛选一致（含当天、排除未知日期）",
            expectedDate.Count == 1 && GridMatches(expectedDate));

        // 清空筛选恢复全量，验证多选与排序稳定性（TC-08）。
        _dateEnabledCheck.Checked = false;
        _applyFilterButton.PerformClick();
        Check("清空筛选后恢复全量列表", _sessionGrid.Rows.Count == expected.Sessions.Count);

        _sessionGrid.ClearSelection();
        _sessionGrid.Rows[0].Selected = true;
        _sessionGrid.Rows[2].Selected = true;
        var selectedBefore = SelectedSessionIds();
        Check("多选两条整会话且删除按钮启用", selectedBefore.Count == 2 && _deleteButton.Enabled);

        _sessionGrid.Sort(_sessionGrid.Columns["size"]!, System.ComponentModel.ListSortDirection.Ascending);
        var selectedAfter = SelectedSessionIds();
        Check("列排序后多选仍指向原会话（不指向别的会话）",
            selectedBefore.Count == selectedAfter.Count && selectedBefore.Zip(selectedAfter).All(static p => p.First == p.Second));

        _sessionGrid.ClearSelection();
        Check("取消选择后删除按钮禁用", !_deleteButton.Enabled);
    }

    private List<string?> SelectedSessionIds() =>
        _sessionGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(static row => ((SessionSummary)row.Tag!).SessionId)
            .OrderBy(static id => id ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    private bool GridMatches(IReadOnlyList<SessionSummary> expected)
    {
        if (_sessionGrid.Rows.Count != expected.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var row = _sessionGrid.Rows[i];
            var session = expected[i];
            var tag = row.Tag as SessionSummary;
            if (tag is null
                || !string.Equals(tag.SessionId, session.SessionId, StringComparison.Ordinal)
                || tag.TotalBytes != session.TotalBytes
                || !Equals(tag.Status, session.Status)
                || (string?)row.Cells["id"].Value != (session.SessionId ?? "（未知）")
                || (string?)row.Cells["size"].Value != InventoryPresenter.FormatBytes(session.TotalBytes))
            {
                return false;
            }
        }

        return true;
    }

    private void Check(string name, bool passed)
    {
        _smokeLines.Add((passed ? "PASS " : "FAIL ") + name);
        _smokeFailed |= !passed;
    }
}
