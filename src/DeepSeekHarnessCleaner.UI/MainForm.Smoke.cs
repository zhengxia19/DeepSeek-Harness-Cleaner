using System.Drawing;
using System.Windows.Forms;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-17 冒烟自检：以 <c>--smoke &lt;结果文件&gt;</c> 启动时，窗口显示后自动执行布局/键盘导航断言，
/// 写入逐项 PASS/FAIL 结果文件并退出。用于在实机上取证“窗口启动退出、缩放后主操作按钮不被遮挡、键盘可达”。
/// 字体按 1.25/1.5 倍缩放用于模拟 125%/150% DPI 下的布局（AutoScaleMode.Font 会整体重排）；
/// 干净 VM 上的真实 DPI 实机验证由 T-33 执行。
/// </summary>
public sealed partial class MainForm
{
    private readonly string? _smokeResultPath;
    private readonly List<string> _smokeLines = new();
    private bool _smokeFailed;

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
            _menu.Visible && _sessionList.Visible && _preview.Visible
            && _actionPanel.Visible && _statusStrip.Visible);
        Check("列表区与预览区并排且不压住按钮区",
            _split.Bottom <= _actionPanel.Top && _split.Top >= _menu.Bottom);

        VerifyActionButtonsFit("100%");
        VerifyKeyboardNavigation();

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
            _sessionList.TabIndex < _preview.TabIndex
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
        _sessionList.Focus();
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

    private void Check(string name, bool passed)
    {
        _smokeLines.Add((passed ? "PASS " : "FAIL ") + name);
        _smokeFailed |= !passed;
    }
}
