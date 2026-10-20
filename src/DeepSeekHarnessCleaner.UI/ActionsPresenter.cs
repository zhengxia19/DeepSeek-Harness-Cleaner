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
/// 对话框可用的异步操作；删除只在用户确认后调用 executor，备份可选。
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
        new SessionCleanupPreparation().Build(root, selected);

    public ManifestBuildResult BuildSessionManifest(string root, IReadOnlyList<SessionSummary> selected,
        IReadOnlyList<string>? attachmentPaths = null) =>
        new BackupManifestBuilder().Build(root, selected, attachmentPaths);

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
        BackupManifest? manifest,
        VerifiedBackup? backup,
        string root,
        bool userConfirmed,
        CancellationToken ct) =>
        Task.Run(() => _executor.Execute(plan, manifest, backup, root, userConfirmed, ct));

    /// <summary>
    /// T-31：把执行结果 + 计划 + 备份事实组装为可展示/可脱敏日志的执行报告。
    /// 逐项按路径（不区分大小写）回查计划目标，取类别与所属会话；查不到按 SessionLog 保守标注。
    /// </summary>
    public OutcomeReport BuildReport(
        CleanupPlan plan,
        VerifiedBackup? backup,
        CleanupExecutionResult result,
        string zipPath)
    {
        var byPath = plan.Targets.ToDictionary(static t => t.AbsolutePath, StringComparer.OrdinalIgnoreCase);
        var items = new List<OutcomeItem>(result.Outcomes.Count);
        foreach (var outcome in result.Outcomes)
        {
            byPath.TryGetValue(outcome.AbsolutePath, out var target);
            items.Add(new OutcomeItem(
                outcome.AbsolutePath,
                target?.Category ?? CleanupCategory.SessionLog,
                target?.OwnerSessionId,
                MapStatus(outcome.Status),
                outcome.Reason,
                outcome.DeletedBytes));
        }

        return new OutcomeReport(new OutcomeReportInput(
            result.Aborted,
            result.AbortCode,
            backup?.ZipPath,
            backup?.ManifestSignature,
            backup?.FileEntryCount ?? 0,
            backup?.TotalBytes ?? 0,
            items));
    }

    private static OutcomeItemStatus MapStatus(ItemStatus status) => status switch
    {
        ItemStatus.Deleted => OutcomeItemStatus.Deleted,
        ItemStatus.Skipped => OutcomeItemStatus.Skipped,
        ItemStatus.Failed => OutcomeItemStatus.Failed,
        _ => OutcomeItemStatus.Failed,
    };
}
