using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// T-31 执行结果对话框（REQ-016，TC-16）：显示每项成功/跳过/失败、实际释放字节、
/// 备份位置（完整路径仅在此 UI 出现）与错误码；与 <see cref="OutcomeReport"/> 同源。
/// 打开时把 <see cref="OutcomeReport.BuildLog"/> 的脱敏结构化日志写入日志目录
/// （默认 %LOCALAPPDATA%\DeepSeekHarnessCleaner\logs，可由 --log-dir 注入夹具目录），
/// 日志不含会话正文、路径或自由文本。纯内存渲染，零网络（TC-18）。
/// </summary>
public sealed class ResultsDialog : Form
{
    private readonly Label _summaryLabel = new();
    private readonly Label _backupLabel = new();
    private readonly DataGridView _itemGrid = new();
    private readonly Button _closeButton = new();
    private readonly Label _logLabel = new();
    private readonly OutcomeReport _report;
    private readonly string? _logDirectory;

    public ResultsDialog(OutcomeReport report, string? logDirectory = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _logDirectory = logDirectory;

        Text = "执行结果报告";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(720, 460);

        _summaryLabel.AutoSize = true;
        _summaryLabel.Location = new Point(12, 10);
        _summaryLabel.Text = _report.Aborted
            ? $"已中止：{_report.AbortCode}（未执行任何删除）"
            : $"删除 {_report.DeletedCount} 个 / 跳过 {_report.SkippedCount} 个 / 失败 {_report.FailedCount} 个，" +
              $"实际释放 {InventoryPresenter.FormatBytes(_report.FreedBytes)}。";

        _backupLabel.AutoSize = true;
        _backupLabel.Location = new Point(12, 34);
        _backupLabel.Text = _report.BackupZipPath is { Length: > 0 } zip
            ? $"备份：{zip}（{_report.BackupEntryCount} 个文件，{InventoryPresenter.FormatBytes(_report.BackupTotalBytes)}，" +
              $"清单签名 {_report.BackupSignature}）"
            : "备份：本次无 ZIP 备份。";

        _itemGrid.Location = new Point(12, 60);
        _itemGrid.Size = new Size(696, 330);
        _itemGrid.AllowUserToAddRows = false;
        _itemGrid.AllowUserToDeleteRows = false;
        _itemGrid.ReadOnly = true;
        _itemGrid.RowHeadersVisible = false;
        _itemGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _itemGrid.MultiSelect = false;
        _itemGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件", FillWeight = 320 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类别", FillWeight = 70 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "会话", FillWeight = 90 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 50 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "错误码", FillWeight = 90 });
        _itemGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "释放字节", FillWeight = 60 });
        foreach (var item in _report.Items)
        {
            _itemGrid.Rows.Add(
                item.DisplayPath,
                item.Category.ToString(),
                item.OwnerSessionId ?? "-",
                StatusText(item.Status),
                item.ErrorCode ?? "-",
                item.FreedBytes.ToString(CultureInfo.InvariantCulture));
        }

        _closeButton.Text = "关闭(&L)";
        _closeButton.AutoSize = true;
        _closeButton.Location = new Point(620, 400);
        _closeButton.TabIndex = 10;
        _closeButton.Click += (_, _) => Close();

        _logLabel.AutoSize = true;
        _logLabel.Location = new Point(12, 404);
        _logLabel.Text = "脱敏日志：写入中…";

        Controls.AddRange(new Control[] { _summaryLabel, _backupLabel, _itemGrid, _closeButton, _logLabel });
        Shown += OnShownWriteLog;
    }

    /// <summary>已写入的脱敏日志文件路径（写入失败为 null）。</summary>
    public string? LogFilePath { get; private set; }

    /// <summary>UI 逐项行数：必须与报告逐项数量一致（冒烟一致性断言）。</summary>
    public int DisplayedItemCount => _itemGrid.Rows.Count;

    public string SummaryText => _summaryLabel.Text;

    public string BackupText => _backupLabel.Text;

    private void OnShownWriteLog(object? sender, EventArgs e)
    {
        try
        {
            var directory = _logDirectory is { Length: > 0 }
                ? _logDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarnessCleaner", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"outcome-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(path, _report.BuildLog());
            LogFilePath = path;
            _logLabel.Text = "脱敏日志已写入：" + path;
        }
        catch (Exception ex)
        {
            _logLabel.Text = "脱敏日志写入失败：" + ex.Message;
        }
    }

    private static string StatusText(OutcomeItemStatus status) => status switch
    {
        OutcomeItemStatus.Deleted => "已删除",
        OutcomeItemStatus.Skipped => "已跳过",
        OutcomeItemStatus.Failed => "失败",
        _ => status.ToString(),
    };
}
