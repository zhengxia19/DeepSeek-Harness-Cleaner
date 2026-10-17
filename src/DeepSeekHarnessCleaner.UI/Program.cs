using System.Windows.Forms;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 应用入口：正常模式启动主窗体；<c>--smoke &lt;结果文件&gt;</c> 模式启动后自动执行布局自检并退出（T-17 取证用）。
/// </summary>
internal static class Program
{
    private const string SmokeSwitch = "--smoke";

    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var isSmoke = args.Length >= 2 && args[0] == SmokeSwitch;
        var smokeResultPath = isSmoke ? Path.GetFullPath(args[1]) : null;

        using var form = new MainForm(smokeResultPath);
        System.Windows.Forms.Application.Run(form);
        return form.SmokeExitCode;
    }
}
