using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>备份阶段（T-30）。</summary>
public enum BackupStage
{
    None,
    Building,
    Ready,
    Failed,
    Canceled,
}

/// <summary>备份对话框的可观察状态。</summary>
public sealed record BackupUiState(
    BackupStage Stage,
    int EntryCount,
    long TotalBytes,
    string? ZipPath,
    string? ErrorCode,
    string? Message);

/// <summary>
/// 备份/删除操作的演示逻辑（T-30）：把 Application/Infrastructure 契约
/// （CleanupPlanner、BackupManifestBuilder、BackupService、CleanupExecutor）包装成
/// 对话框可用的异步操作；删除只在用户确认后调用 executor，且备份先行。
/// </summary>
public sealed class ActionsPresenter
{
    private readonly BackupService _backupService = new();
    private readonly CleanupExecutor _executor;
    private readonly HarnessProcessProbe _probe;

    public ActionsPresenter(Func<IReadOnlyList<ProcessSnapshot>>? processSnapshotOverride = null)
    {
        _probe = new HarnessProcessProbe(processSnapshotOverride);
        _executor = new CleanupExecutor(_probe);
    }

    public CleanupPlanResult BuildSessionPlan(string root, IReadOnlyList<SessionSummary> selected) =>
        new CleanupPlanner().Build(root, selected, []);

    public ManifestBuildResult BuildSessionManifest(string root, IReadOnlyList<SessionSummary> selected) =>
        new BackupManifestBuilder().Build(root, selected);

    public HarnessProcessProbeResult ProbeProcesses() => _probe.Probe();

    /// <summary>后台创建并校验 ZIP 备份（原始文件全量入包；取消经 ct 传播）。</summary>
    public Task<BackupResult> RunBackupAsync(BackupManifest manifest, string zipPath, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                return _backupService.CreateBackup(manifest, zipPath, ct);
            },
            ct);

    /// <summary>后台执行删除（executor 内部再门禁：确认/备份/进程/指纹 + 逐项复验）。
    /// ct 只传给 Execute（逐项 CANCELED 跳过），不传给 Task.Run，避免任务尚未启动即整体取消丢失逐项结果。</summary>
    public Task<CleanupExecutionResult> RunCleanupAsync(
        CleanupPlan plan,
        BackupManifest manifest,
        VerifiedBackup backup,
        string root,
        bool userConfirmed,
        CancellationToken ct) =>
        Task.Run(() => _executor.Execute(plan, manifest, backup, root, userConfirmed, ct));
}
