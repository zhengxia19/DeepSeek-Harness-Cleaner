using System.Diagnostics;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>进程探测三态（design.md §7.6）：仅 Inactive 允许进入写入；Unknown 一律 fail closed。</summary>
public enum HarnessProcessState
{
    /// <summary>至少一个可验证的已知 Harness 进程在运行。</summary>
    Active,

    /// <summary>枚举成功且无任何已知/可疑匹配：允许写入。</summary>
    Inactive,

    /// <summary>枚举失败、权限不足或匹配进程路径不可验证：禁止写入。</summary>
    Unknown,
}

/// <summary>匹配到的已知 Harness 进程事实（只读，不含任何停止动作）。</summary>
public sealed record HarnessProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath);

/// <summary>探测结果：RiskNotes 明确记录无法可靠识别的托管/子进程风险（design.md §7.6）。</summary>
public sealed record HarnessProcessProbeResult(
    HarnessProcessState State,
    IReadOnlyList<HarnessProcessInfo> Matches,
    IReadOnlyList<string> RiskNotes,
    string? ErrorCode)
{
    /// <summary>写入门禁：仅 Inactive 可进入（TC-15）。</summary>
    public bool AllowsWrite => State == HarnessProcessState.Inactive;
}

/// <summary>
/// 进程快照事实（可注入以便测试模拟 Active/Inactive/Unknown）。
/// <paramref name="PathAccessible"/>=false 表示进程存在但主模块路径不可验证（权限/位数差异）。
/// </summary>
public sealed record ProcessSnapshot(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    bool PathAccessible);

/// <summary>
/// T-25 Harness 退出状态检查（TC-15）：通过已知 exe 名与可验证进程路径检测 Harness；
/// 无法判定时返回 Unknown（fail closed）。只读探测：绝不 Kill/CloseMainWindow。
/// </summary>
public sealed class HarnessProcessProbe
{
    /// <summary>design.md §7.6 已知可执行文件名（ProcessName 不含扩展名）。</summary>
    public const string KnownExecutableName = "DeepSeek Harness.exe";
    public const string KnownProcessName = "DeepSeek Harness";

    public const string EnumerationFailedErrorCode = "PROCESS_ENUMERATION_FAILED";

    private const string ManagedProcessRiskNote =
        "托管/子进程可能不以已知 exe 名出现，无法可靠识别；进程退出探测不是绝对文件锁。";

    private readonly Func<IReadOnlyList<ProcessSnapshot>>? _snapshotOverride;

    /// <summary>默认使用真实系统枚举；测试可注入快照源模拟三态。</summary>
    public HarnessProcessProbe(Func<IReadOnlyList<ProcessSnapshot>>? snapshotOverride = null) =>
        _snapshotOverride = snapshotOverride;

    public HarnessProcessProbeResult Probe()
    {
        IReadOnlyList<ProcessSnapshot> snapshots;
        try
        {
            snapshots = _snapshotOverride is null ? TakeRealSnapshots() : _snapshotOverride();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new HarnessProcessProbeResult(
                HarnessProcessState.Unknown, [], [ManagedProcessRiskNote], EnumerationFailedErrorCode);
        }

        var matches = new List<HarnessProcessInfo>();
        var risks = new List<string> { ManagedProcessRiskNote };
        var unknown = false;

        foreach (var snapshot in snapshots)
        {
            var nameMatch = string.Equals(snapshot.ProcessName, KnownProcessName, StringComparison.OrdinalIgnoreCase);
            var pathMatch = snapshot.ExecutablePath is not null
                && string.Equals(
                    Path.GetFileName(snapshot.ExecutablePath),
                    KnownExecutableName,
                    StringComparison.OrdinalIgnoreCase);

            if (!nameMatch && !pathMatch)
            {
                continue;
            }

            if (!nameMatch)
            {
                // 进程名不同但主模块是已知 exe（改名/包装）：按已知进程计入，并记录风险。
                risks.Add($"进程 {snapshot.ProcessId} 名称与已知 exe 不符但主模块匹配：{snapshot.ProcessName}");
            }

            if (!snapshot.PathAccessible || snapshot.ExecutablePath is null)
            {
                // 名称匹配但路径不可验证：不能确认是否为本授权布局的 Harness，fail closed。
                unknown = true;
                risks.Add($"进程 {snapshot.ProcessId}（{snapshot.ProcessName}）主模块路径不可验证，无法确认身份");
                continue;
            }

            matches.Add(new HarnessProcessInfo(snapshot.ProcessId, snapshot.ProcessName, snapshot.ExecutablePath));
        }

        var state = matches.Count > 0
            ? HarnessProcessState.Active
            : unknown
                ? HarnessProcessState.Unknown
                : HarnessProcessState.Inactive;

        return new HarnessProcessProbeResult(state, matches, risks, null);
    }

    /// <summary>真实枚举：ProcessName 与 MainModule 访问都可能抛异常；逐进程降级为 PathAccessible=false。</summary>
    private static IReadOnlyList<ProcessSnapshot> TakeRealSnapshots()
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                string? path = null;
                var pathAccessible = false;
                try
                {
                    path = process.MainModule?.FileName;
                    pathAccessible = path is not null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 32/64 位差异或权限不足：路径不可验证，身份判定降级。
                }

                result.Add(new ProcessSnapshot(process.Id, name, path, pathAccessible));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 枚举期间进程退出：跳过该快照（整体枚举仍视为成功）。
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}
