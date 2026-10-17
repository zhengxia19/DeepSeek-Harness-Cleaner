using System.Globalization;
using System.Security;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>
/// UI 侧扫描源适配器（T-18）：SessionLogDiscovery + SessionHeaderReader → Domain 扫描事实。
/// UI 是组合根，允许引用 Infrastructure；与测试侧适配器同构。
/// </summary>
public sealed class UiScanSource : ISessionScanSource
{
    private readonly SessionLogDiscovery _discovery = new(new PathGuard());
    private readonly SessionHeaderReader _reader = new();

    /// <summary>最近一次 Scan 实际运行的托管线程 id（冒烟自检据此证明扫描不在 UI 线程执行）。</summary>
    public int LastScanThreadId { get; private set; } = -1;

    public SessionScanData Scan(string sessionRoot)
    {
        LastScanThreadId = Environment.CurrentManagedThreadId;
        var discovered = _discovery.Discover(sessionRoot);
        if (!discovered.RootValid)
            return new SessionScanData(discovered.RootErrorCode, [], []);

        var sessions = new List<ScannedSessionDirectory>();
        foreach (var candidate in discovered.Candidates)
        {
            var dirIdentity = Path.GetFileName(candidate.RelativeDirectory);
            var files = new List<ScannedLogFile>();
            foreach (var file in candidate.Files)
            {
                var header = _reader.Read(file.FullPath, dirIdentity);
                var log = new PhysicalLog(
                    file.FullPath,
                    file.Generation,
                    file.Encoding == SessionFileEncoding.Zstd,
                    file.Length,
                    SafeModifiedUtc(file.FullPath),
                    Sha256: null,
                    header.Status);
                files.Add(new ScannedLogFile(
                    log,
                    header.Header?.SessionId,
                    header.Header?.CreatedAt,
                    header.IdentityMatches == false));
            }

            sessions.Add(new ScannedSessionDirectory(
                candidate.RelativeDirectory,
                files,
                candidate.ConflictingGenerations));
        }

        var anomalies = new List<AnomalousFile>();
        foreach (var anomaly in discovered.Anomalies)
        {
            long? bytes = null;
            try
            {
                if (File.Exists(anomaly.FullPath))
                    bytes = new FileInfo(anomaly.FullPath).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            anomalies.Add(new AnomalousFile(anomaly.FullPath, anomaly.RelativePath, anomaly.Reason, bytes));
        }

        return new SessionScanData(null, sessions, anomalies);
    }

    private static DateTimeOffset SafeModifiedUtc(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (SecurityException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// 会话库存演示器（T-18）：后台执行扫描、缓存结果、按 Domain/Application 契约做筛选，
/// 并提供可解释的状态栏总量文本。不依赖任何 WinForms 类型，便于冒烟自检直接核对。
/// </summary>
public sealed class InventoryPresenter
{
    private readonly InventoryService _inventory;
    private readonly TimeZoneInfo _timeZone;

    public InventoryPresenter(UiScanSource source, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeZone);
        ScanSource = source;
        _inventory = new InventoryService(source);
        _timeZone = timeZone;
    }

    public UiScanSource ScanSource { get; }

    public InventoryResult? LastResult { get; private set; }

    public IReadOnlyList<SessionSummary> Sessions => LastResult?.Sessions ?? [];

    /// <summary>后台线程执行扫描（UI 不冻结）；完成后缓存结果。</summary>
    public Task<InventoryResult> ScanAsync(string sessionRoot, CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                var result = _inventory.Scan(sessionRoot, ct);
                LastResult = result;
                return result;
            },
            ct);

    public IReadOnlyList<SessionSummary> ApplyFilter(FilterRequest request) =>
        SessionFilter.Apply(Sessions, request, _timeZone);

    public TimeZoneInfo TimeZone => _timeZone;

    public static string FormatBytes(long bytes) =>
        bytes.ToString("N0", CultureInfo.CurrentCulture) + " B"
        + (bytes >= 1024 ? $"（{bytes / 1024.0:0.##} KiB）" : string.Empty);

    /// <summary>状态栏总量文本：件数/合计容量/异常文件数（TC-05 可解释统计），筛选时追加筛选条数。</summary>
    public string BuildStatusText(IReadOnlyList<SessionSummary>? filtered)
    {
        var result = LastResult;
        if (result is null)
            return "尚未扫描。";
        if (result.RootErrorCode is not null)
            return $"扫描失败：{result.RootErrorCode}";

        var text = $"共 {result.Sessions.Count} 个会话，合计 {FormatBytes(result.TotalBytes)}；异常文件 {result.AnomalousFiles.Count} 个";
        if (filtered is not null)
            text += $"；筛选后 {filtered.Count} 条";
        return text;
    }
}
