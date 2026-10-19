using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-17/T-18 冒烟自检：以 <c>--smoke &lt;结果文件&gt; [--scan-root &lt;目录&gt;]</c> 启动时，窗口显示后自动执行
/// 布局/键盘导航断言；提供 --scan-root 时额外执行数据绑定断言（后台扫描、列表/统计/筛选与服务结果一致、
/// 排序不改变勾选指向的会话、序号随行），写入逐项 PASS/FAIL 结果文件并退出。
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
            RunPreviewChecks();
            RunActionsChecks(_scanRootOverride);
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

        Check($"{scaleLabel} 删除按钮初始禁用（未勾选会话）", !_deleteButton.Enabled);
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
    /// 验证列排序后勾选仍指向原会话、序号随行（N-02）。
    /// </summary>
    private void RunBindingChecks(string fixtureRoot)
    {
        var uiThreadId = Environment.CurrentManagedThreadId;

        // 服务侧期望结果：与 UI 同一套 Application/Infrastructure 契约，独立实例。
        var expectedService = new InventoryService(new UiScanSource());
        var expected = expectedService.Scan(fixtureRoot);
        Check("fixture 服务扫描成功（305 会话 / 1 异常文件）",
            expected.RootErrorCode is null && expected.Sessions.Count == 305 && expected.AnomalousFiles.Count == 1);

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

        // N-01：归档列与状态栏归档统计（fixture 副本含 storages\workspace.json，归档信息可用）。
        var expectedArchived = expected.Sessions.Count(static s => s.IsArchived);
        var archivedCells = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Count(static row => (string?)row.Cells["archive"].Value == "是");
        Check("归档列命中数与扫描结果一致（归档信息可用且命中≥1）",
            expected.ArchiveStatus is null && expectedArchived >= 1 && archivedCells == expectedArchived);
        Check("状态栏归档计数与服务统计一致",
            status.Contains($"归档 {expectedArchived}", StringComparison.Ordinal));

        // N-02：列序与勾选/序号列属性断言。
        var columnNames = string.Join(",", _sessionGrid.Columns.Cast<DataGridViewColumn>().Select(static c => c.Name));
        Check("列序：勾选/序号/会话 ID/项目/创建时间/日期来源/大小/格式/状态/归档",
            columnNames == "check,num,id,project,created,origin,size,format,status,archive");
        var checkColumn = _sessionGrid.Columns["check"];
        var numColumn = _sessionGrid.Columns["num"];
        _smokeLines.Add($"INFO 列属性 check={checkColumn?.GetType().Name} sort={checkColumn?.SortMode} ro={checkColumn?.ReadOnly}；num={numColumn?.GetType().Name} sort={numColumn?.SortMode} ro={numColumn?.ReadOnly}");
        Check("勾选列为复选框列、不可排序、可编辑；序号列只读、不可排序",
            checkColumn is DataGridViewCheckBoxColumn
            && checkColumn.SortMode == DataGridViewColumnSortMode.NotSortable
            && !checkColumn.ReadOnly
            && numColumn is DataGridViewTextBoxColumn
            && numColumn.ReadOnly
            && numColumn.SortMode == DataGridViewColumnSortMode.NotSortable);

        // N-02：project/created/size 三列宽度 ≥ 全行最长内容（含表头；fixture 含完整日期时间/多档字节值）。
        var widthOk = true;
        foreach (var name in new[] { "project", "created", "size" })
        {
            var column = _sessionGrid.Columns[name]!;
            var widest = PreferredCellWidth(column.HeaderCell.InheritedStyle, column.HeaderText);
            foreach (DataGridViewRow row in _sessionGrid.Rows)
            {
                var cell = row.Cells[column.Index];
                widest = Math.Max(widest, PreferredCellWidth(cell.InheritedStyle, cell.Value as string ?? string.Empty));
            }

            widthOk &= column.Width >= widest;
            _smokeLines.Add($"INFO 列 {name} 宽度 {column.Width}，全行最长内容首选宽度 {widest}");
        }

        Check("project/created/size 三列宽度 ≥ 最长内容（全行测量含表头）", widthOk);

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

        // 清空筛选恢复全量，验证勾选语义、序号随行与排序稳定性（TC-08，N-02）。
        _dateEnabledCheck.Checked = false;
        _applyFilterButton.PerformClick();
        Check("清空筛选后恢复全量列表", _sessionGrid.Rows.Count == expected.Sessions.Count);

        _sessionGrid.ClearSelection();
        _sessionGrid.Rows[0].Cells["check"].Value = true;
        _sessionGrid.Rows[2].Cells["check"].Value = true;
        var checkedBefore = CheckedSessionIds();
        Check("勾选两行启用删除按钮（勾选语义）", checkedBefore.Count == 2 && _deleteButton.Enabled);

        var numBefore = NumBySessionId();
        _sessionGrid.Sort(_sessionGrid.Columns["size"]!, System.ComponentModel.ListSortDirection.Ascending);
        var checkedAfter = CheckedSessionIds();
        var numAfter = NumBySessionId();
        Check("列排序后勾选仍指向原会话（不指向别的会话）",
            checkedBefore.Count == checkedAfter.Count && checkedBefore.Zip(checkedAfter).All(static p => p.First == p.Second));
        Check("排序后序号随行（序号仍为绑定顺序，不随显示位置重排）",
            numBefore.Count == numAfter.Count && numBefore.SequenceEqual(numAfter));

        foreach (DataGridViewRow row in _sessionGrid.Rows)
        {
            row.Cells["check"].Value = false;
        }

        Check("取消勾选后删除按钮禁用", !_deleteButton.Enabled);
    }

    /// <summary>
    /// T-19 预览断言（TC-06/TC-20）：V4 金样（代际 4，fixture 确证）显示 role/文本/预览代际且投影完整；
    /// 未知旧代际（v7）显示元数据与提示而非空白/崩溃；坏头部会话显示不完整事件标记；
    /// 快速重选（先选 A 立即改选 B，中间不泵消息）后预览只呈现新会话，不串会话。
    /// </summary>
    private void RunPreviewChecks()
    {
        SelectRowBySessionId("smoke-v4-0004");
        PumpUntil(() => _preview.Text.Contains("V4 question", StringComparison.Ordinal), 10_000);
        Check("V4 金样预览显示 user/assistant 角色与文本正确",
            _preview.Text.Contains("[user]", StringComparison.Ordinal)
            && _preview.Text.Contains("V4 question", StringComparison.Ordinal)
            && _preview.Text.Contains("[assistant]", StringComparison.Ordinal)
            && _preview.Text.Contains("V4 answer", StringComparison.Ordinal));
        Check("V4 金样预览显示预览代际 4 且投影完整",
            _preview.Text.Contains("预览代际：4", StringComparison.Ordinal)
            && _preview.Text.Contains("投影完整", StringComparison.Ordinal));

        SelectRowBySessionId("smoke-old-0005");
        PumpUntil(() => _preview.Text.Contains("会话：smoke-old-0005", StringComparison.Ordinal), 10_000);
        Check("未知旧代际 v7 显示元数据与提示而非空白/崩溃",
            _preview.Text.Contains("会话：smoke-old-0005", StringComparison.Ordinal)
            && _preview.Text.Contains("预览模式：MetadataOnly", StringComparison.Ordinal)
            && _preview.Text.Contains("未经脱敏 fixture 确证", StringComparison.Ordinal)
            && _preview.Text.Contains("无事件正文", StringComparison.Ordinal));

        SelectRowBySessionId(null); // 坏头部会话（SessionId 未知）。
        PumpUntil(() => _preview.Text.Contains("不完整事件", StringComparison.Ordinal), 10_000);
        Check("坏头部会话显示不完整事件标记（不空白/不崩溃）",
            _preview.Text.Contains("不完整事件：第 1 行", StringComparison.Ordinal)
            && _preview.Text.Contains("预览模式：Projected", StringComparison.Ordinal));

        // 取消/重选不串会话：选中 A 后立即改选 B（中间不泵消息），最终只呈现 B。
        SelectRowBySessionId("smoke-a-0001");
        SelectRowBySessionId("smoke-b-0002");
        PumpUntil(() => _preview.Text.Contains("会话：smoke-b-0002", StringComparison.Ordinal), 10_000);
        Check("重选后预览不串会话（仅呈现新会话，旧会话内容不出现）",
            _preview.Text.Contains("会话：smoke-b-0002", StringComparison.Ordinal)
            && !_preview.Text.Contains("会话：smoke-a-0001", StringComparison.Ordinal));

        _sessionGrid.ClearSelection();
        PumpUntil(() => _preview.Text.Length == 0, 5_000);
        Check("取消选择后预览清空", _preview.Text.Length == 0);
    }

    /// <summary>T-30/N-04：备份/删除对话框流程断言（取消不删除、未备份可直接确认、已备份校验后才删、确认后精确删除、完成刷新、取消执行不阻塞 UI）。</summary>
    private void RunActionsChecks(string fixtureRoot)
    {
        var service = new InventoryService(new UiScanSource());
        var scan = service.Scan(fixtureRoot);
        var sessionA = scan.Sessions.First(static s => s.SessionId == "smoke-a-0001");
        var sessionB = scan.Sessions.First(static s => s.SessionId == "smoke-b-0002");
        var untouched = scan.Sessions.First(static s => s.SessionId == "smoke-v4-0004");
        var expectedPlan = new CleanupPlanner().Build(fixtureRoot, [sessionA, sessionB], []).Plan!;

        Check("删除计划：两会话全部代际文件、备份可选（不强制）",
            expectedPlan.Targets.Count >= 2 && !expectedPlan.RequiresBackup);
        Check("删除前目标文件全部存在", expectedPlan.Targets.All(static t => File.Exists(t.AbsolutePath)));

        _sessionGrid.ClearSelection();
        SetCheckBySessionId("smoke-a-0001", true);
        SetCheckBySessionId("smoke-b-0002", true);
        _deleteButton.PerformClick();
        System.Windows.Forms.Application.DoEvents();
        var dialog = _lastCleanupDialog!;
        Check("删除确认对话框打开", dialog.Visible);
        Check("删除计划仅含勾选两会话（未勾选行不进入计划）",
            dialog.Plan.Targets.Select(static t => t.AbsolutePath).OrderBy(static p => p, StringComparer.Ordinal)
                .SequenceEqual(expectedPlan.Targets.Select(static t => t.AbsolutePath).OrderBy(static p => p, StringComparer.Ordinal)));
        Check("未备份时确认删除按钮可用（备份可选，N-04）", dialog.ConfirmButtonEnabled);

        dialog.RequestCancel();
        PumpUntil(() => !dialog.Visible, 5_000);
        Check("点击取消即关闭且不删除任何文件",
            !dialog.Visible && expectedPlan.Targets.All(static t => File.Exists(t.AbsolutePath)));

        _deleteButton.PerformClick();
        System.Windows.Forms.Application.DoEvents();
        var dialog2 = _lastCleanupDialog!;
        Check("重新打开为新的对话框实例", dialog2.Visible && !ReferenceEquals(dialog2, dialog));
        dialog2.RequestBackup();
        PumpUntil(() => dialog2.BackupReady, 20_000);
        _smokeLines.Add($"INFO 备份状态：{dialog2.BackupStatusText}");
        Check("备份完成并校验后确认删除按钮启用", dialog2.BackupReady && dialog2.ConfirmButtonEnabled);

        var pumps = 0;
        dialog2.RequestConfirm();
        var start = Environment.TickCount64;
        while (!dialog2.RunFinished && Environment.TickCount64 - start < 30_000)
        {
            System.Windows.Forms.Application.DoEvents();
            pumps++;
        }

        var result = dialog2.LastResult;
        Check("确认后执行器精确删除全部选定目标",
            result is { Aborted: false } && result.DeletedCount == expectedPlan.Targets.Count && result.AllSucceeded);
        Check("已选会话文件删除、未选会话文件不变",
            expectedPlan.Targets.All(static t => !File.Exists(t.AbsolutePath))
            && untouched.Generations.All(static g => File.Exists(g.FullPath)));
        Check("删除在后台执行且消息泵持续运行（UI 未阻塞）", pumps >= 1);

        PumpUntil(() => (_statusLabel.Text ?? string.Empty).Contains("已刷新", StringComparison.Ordinal), 20_000);
        Check("删除完成后清单自动刷新（会话数减少 2）", _sessionGrid.Rows.Count == scan.Sessions.Count - 2);

        // TC-16：执行中取消 → 逐项 CANCELED 跳过，正常收尾不异常。
        _sessionGrid.ClearSelection();
        for (var i = 0; i < 60 && i < _sessionGrid.Rows.Count; i++)
        {
            _sessionGrid.Rows[i].Cells["check"].Value = true;
        }

        _deleteButton.PerformClick();
        System.Windows.Forms.Application.DoEvents();
        var dialog3 = _lastCleanupDialog!;
        var prevResultsDialog = _lastResultsDialog; // dialog2 全成功报告（若有）；dialog3 终态后应替换为新实例。
        dialog3.RequestBackup();
        PumpUntil(() => dialog3.BackupReady, 60_000);
        Check("60 会话批量备份完成", dialog3.BackupReady);
        dialog3.RequestConfirm();
        dialog3.RequestCancel();
        var pumps3 = 0;
        start = Environment.TickCount64;
        while (!dialog3.RunFinished && Environment.TickCount64 - start < 60_000)
        {
            System.Windows.Forms.Application.DoEvents();
            pumps3++;
        }

        var result3 = dialog3.LastResult;
        Check("取消执行正常收尾（部分删除 + 取消跳过，无异常，UI 持续响应）",
            result3 is { Aborted: false } && (result3.SkippedCount > 0 || result3.DeletedCount > 0) && pumps3 >= 1);

        // T-31：部分失败场景下结果报告 UI 与脱敏日志一致，且日志不泄露正文/路径。
        if (result3 is not null)
        {
            PumpUntil(() => !ReferenceEquals(_lastResultsDialog, prevResultsDialog), 20_000);
            RunResultsChecks(result3, fixtureRoot);
        }

        // 复位勾选与选择，保证后续缩放回归检查仍见“删除按钮初始禁用（未勾选会话）”。
        _sessionGrid.ClearSelection();
        foreach (DataGridViewRow row in _sessionGrid.Rows)
        {
            row.Cells["check"].Value = false;
        }
    }

    /// <summary>T-31：执行结果报告断言（逐项一致、汇总一致、备份位置可见、脱敏日志无正文/路径泄露）。</summary>
    private void RunResultsChecks(CleanupExecutionResult result, string fixtureRoot)
    {
        var results = _lastResultsDialog;
        Check("执行完成后自动打开结果报告对话框", results is { Visible: true });
        if (results is null)
        {
            return;
        }

        Check("报告逐项行数与执行结果逐项数一致", results.DisplayedItemCount == result.Outcomes.Count);
        Check("报告汇总计数与执行结果一致（部分失败如实显示）",
            results.SummaryText.Contains($"删除 {result.DeletedCount} 个", StringComparison.Ordinal)
            && results.SummaryText.Contains($"跳过 {result.SkippedCount} 个", StringComparison.Ordinal)
            && results.SummaryText.Contains($"失败 {result.FailedCount} 个", StringComparison.Ordinal));
        Check("报告展示备份 ZIP 完整位置", results.BackupText.Contains(".zip", StringComparison.Ordinal));

        PumpUntil(() => results.LogFilePath is not null, 5_000);
        var logPath = results.LogFilePath;
        Check("脱敏日志已写入 --log-dir 夹具目录",
            logPath is not null && File.Exists(logPath)
            && Path.GetDirectoryName(Path.GetFullPath(logPath))
               == Path.GetFullPath(_logDirOverride ?? string.Empty));
        if (logPath is null || !File.Exists(logPath))
        {
            return;
        }

        var logLines = File.ReadAllLines(logPath);
        var itemLines = logLines.Count(static l => l.Contains("event=item", StringComparison.Ordinal));
        Check("日志逐项行数与 UI 逐项行数一致（UI 与日志同源一致）", itemLines == result.Outcomes.Count);
        Check("日志汇总行计数与 UI 汇总一致",
            logLines.Any(l => l.Contains("event=run_summary", StringComparison.Ordinal))
            && logLines.Any(l => l.Contains($" deleted={result.DeletedCount}", StringComparison.Ordinal)
                && l.Contains($" skipped={result.SkippedCount}", StringComparison.Ordinal)
                && l.Contains($" failed={result.FailedCount}", StringComparison.Ordinal)));
        var logText = string.Join('\n', logLines);
        var fixtureName = Path.GetFileName(Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar));
        Check("日志不含 fixture 会话正文（hello smoke）", !logText.Contains("hello smoke", StringComparison.Ordinal));
        Check("日志不含数据根/备份目录路径与用户名",
            !logText.Contains(fixtureName, StringComparison.OrdinalIgnoreCase)
            && !logText.Contains("dshcleaner-backups", StringComparison.OrdinalIgnoreCase)
            && !logText.Contains("Users", StringComparison.OrdinalIgnoreCase)
            && !logText.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectRowBySessionId(string? sessionId)
    {
        _sessionGrid.ClearSelection();
        foreach (DataGridViewRow row in _sessionGrid.Rows)
        {
            if (string.Equals(((SessionSummary)row.Tag!).SessionId, sessionId, StringComparison.Ordinal))
            {
                row.Selected = true;
                return;
            }
        }

        throw new InvalidOperationException("未找到会话行: " + (sessionId ?? "(未知)"));
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
        {
            System.Windows.Forms.Application.DoEvents();
        }
    }

    /// <summary>N-02：当前勾选行对应的会话 ID（按 ID 排序，用于排序前后一致性比较）。</summary>
    private List<string?> CheckedSessionIds() =>
        _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Where(static row => row.Cells["check"].Value is true)
            .Select(static row => ((SessionSummary)row.Tag!).SessionId)
            .OrderBy(static id => id ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    /// <summary>N-02：(会话 ID, 序号) 对按 ID 排序；用于验证排序后序号仍随行（绑定顺序不变）。</summary>
    private List<(string? Id, int Num)> NumBySessionId() =>
        _sessionGrid.Rows.Cast<DataGridViewRow>()
            .Select(static row => ((((SessionSummary)row.Tag!).SessionId, (int)row.Cells["num"].Value!)))
            .OrderBy(static t => t.Item1 ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    private void SetCheckBySessionId(string sessionId, bool value)
    {
        var row = _sessionGrid.Rows.Cast<DataGridViewRow>()
            .First(r => r.Tag is SessionSummary s && s.SessionId == sessionId);
        row.Cells["check"].Value = value;
    }

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
            // N-02：绑定后勾选列初始未选、序号列按绑定顺序 1 起赋值。
            if (tag is null
                || !string.Equals(tag.SessionId, session.SessionId, StringComparison.Ordinal)
                || tag.TotalBytes != session.TotalBytes
                || !Equals(tag.Status, session.Status)
                || tag.IsArchived != session.IsArchived
                || (string?)row.Cells["id"].Value != (session.SessionId ?? "（未知）")
                || (string?)row.Cells["size"].Value != InventoryPresenter.FormatBytes(session.TotalBytes)
                || (string?)row.Cells["archive"].Value != (session.IsArchived ? "是" : "—")
                || row.Cells["check"].Value is not false
                || row.Cells["num"].Value is not int num || num != i + 1)
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
