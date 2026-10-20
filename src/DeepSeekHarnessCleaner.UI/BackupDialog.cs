using System.Drawing;
using System.Windows.Forms;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// 备份对话框（T-30）：显示清单条目数/总字节/目标 ZIP，后台创建并校验备份，可取消；
/// 完成（成功/失败/取消）后触发 <see cref="Completed"/>。删除确认在备份 Ready 之前不可用。
/// </summary>
public sealed class BackupDialog : Form
{
    private readonly TextBox _summaryLabel = new() { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly TextBox _statusLabel = new() { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly Button _cancelButton = new();
    private readonly Button _closeButton = new();
    private readonly Button _openFolderButton = new();
    private readonly ActionsPresenter _presenter;
    private readonly BackupManifest _manifest;
    private readonly string _zipPath;
    private CancellationTokenSource? _cts;

    public BackupDialog(BackupManifest manifest, string zipPath, ActionsPresenter presenter)
    {
        _manifest = manifest;
        _zipPath = zipPath;
        _presenter = presenter;

        Text = "原始文件 ZIP 备份";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(640, 205);

        _summaryLabel.AutoSize = false;
        _summaryLabel.Size = new Size(616, 75);
        _summaryLabel.Location = new Point(12, 12);
        _summaryLabel.Text = $"清单条目：{_manifest.Entries.Count} 个文件；" +
            $"合计 {InventoryPresenter.FormatBytes(_manifest.Entries.Sum(static e => e.Length))}；" +
            $"目标 ZIP：{_zipPath}";

        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(616, 48);
        _statusLabel.Location = new Point(12, 98);
        _statusLabel.Text = "准备中…";

        _cancelButton.Text = "取消(&C)";
        _cancelButton.AutoSize = true;
        _cancelButton.Location = new Point(390, 160);
        _cancelButton.Click += OnCancelClick;

        _closeButton.Text = "关闭(&L)";
        _closeButton.AutoSize = true;
        _closeButton.Location = new Point(520, 160);
        _closeButton.Enabled = false;
        _closeButton.Click += (_, _) => Close();

        _openFolderButton.Text = "打开备份目录(&O)";
        _openFolderButton.AutoSize = true;
        _openFolderButton.Location = new Point(12, 160);
        _openFolderButton.Click += (_, _) =>
        {
            var directory = Path.GetDirectoryName(_zipPath);
            if (directory is not null && Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    ArgumentList = { directory },
                });
            }
        };
        Controls.AddRange(new Control[] { _summaryLabel, _statusLabel, _openFolderButton, _cancelButton, _closeButton });
        Shown += (_, _) => _ = RunAsync();
    }

    public BackupUiState State { get; private set; } = new(BackupStage.None, 0, 0, null, null, null);

    /// <summary>备份进入终态（Ready/Failed/Canceled）后触发。</summary>
    public event EventHandler? Completed;

    private async Task RunAsync()
    {
        State = State with { Stage = BackupStage.Building, EntryCount = _manifest.Entries.Count, ZipPath = _zipPath };
        _statusLabel.Text = "正在创建并校验 ZIP 备份…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await _presenter.RunBackupAsync(_manifest, _zipPath, cts.Token);
            State = result.Success
                ? new BackupUiState(BackupStage.Ready, _manifest.Entries.Count, result.Backup!.TotalBytes, _zipPath, null, null)
                : new BackupUiState(BackupStage.Failed, _manifest.Entries.Count, 0, _zipPath, result.ErrorCode, result.Message);
        }
        catch (OperationCanceledException)
        {
            State = new BackupUiState(BackupStage.Canceled, _manifest.Entries.Count, 0, _zipPath, "CANCELED", "备份已取消。");
        }
        catch (Exception ex)
        {
            State = new BackupUiState(BackupStage.Failed, _manifest.Entries.Count, 0, _zipPath, "UNEXPECTED", ex.Message);
        }

        if (IsDisposed)
        {
            return;
        }

        _statusLabel.Text = State.Stage switch
        {
            BackupStage.Ready => $"备份完成并已校验：{State.ZipPath}（{InventoryPresenter.FormatBytes(State.TotalBytes)}）",
            BackupStage.Canceled => "备份已取消。",
            _ => $"备份失败：{State.ErrorCode} {State.Message}",
        };
        _cancelButton.Enabled = false;
        _closeButton.Enabled = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _cancelButton.Enabled = false;
        _statusLabel.Text = "正在取消…";
    }
}
