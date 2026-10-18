using DeepSeekHarnessCleaner.Application;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// T-31 不泄露正文的执行报告（REQ-016，TC-16/TC-18）：
/// 汇总计数/释放字节正确；部分失败时日志逐项与 UI 数据同源一致；
/// 日志不含路径、会话正文、secret、敏感 token；不安全 token 白名单过滤；纯内存零网络。
/// </summary>
public sealed class OutcomeTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static OutcomeItem Item(
        string path,
        OutcomeItemStatus status,
        string? code = null,
        long bytes = 0,
        string? sessionId = null,
        CleanupCategory category = CleanupCategory.SessionLog) =>
        new(path, category, sessionId, status, code, bytes);

    private static OutcomeReport Report(
        params OutcomeItem[] items) =>
        new(new OutcomeReportInput(false, null, null, null, 0, 0, items));

    [Fact]
    public void Counts_And_FreedBytes_Match_Items()
    {
        var report = Report(
            Item(@"C:\data\sessions\a\session.v4.jsonl", OutcomeItemStatus.Deleted, bytes: 100, sessionId: "a-0001"),
            Item(@"C:\data\sessions\b\session.v4.jsonl", OutcomeItemStatus.Skipped, "CHANGED_LENGTH", sessionId: "b-0002"),
            Item(@"C:\data\sessions\c\session.v4.jsonl", OutcomeItemStatus.Failed, "PERMISSION_DENIED", sessionId: "c-0003"),
            Item(@"C:\data\sessions\d\session.v4.jsonl", OutcomeItemStatus.Deleted, bytes: 250, sessionId: "d-0004"));

        Assert.Equal(2, report.DeletedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(350, report.FreedBytes);
        Assert.True(report.PartialFailure);
    }

    [Fact]
    public void PartialFailure_Log_Matches_Per_Item_Status_And_Code_And_Counts()
    {
        var items = new[]
        {
            Item(@"C:\data\sessions\a\session.v4.jsonl", OutcomeItemStatus.Deleted, bytes: 100, sessionId: "a-0001"),
            Item(@"C:\data\sessions\b\session.v4.jsonl", OutcomeItemStatus.Skipped, "CHANGED_MTIME", sessionId: "b-0002"),
            Item(@"C:\data\attachments\x\附件.bin", OutcomeItemStatus.Failed, "IO_FAILURE", sessionId: null, category: CleanupCategory.Attachment),
        };
        var report = Report(items);
        var log = report.BuildLog(FixedUtc);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 1 行汇总 + 1 行备份 + 每项 1 行。
        Assert.Equal(2 + items.Length, lines.Length);
        var itemLines = lines.Where(static l => l.Contains("event=item", StringComparison.Ordinal)).ToList();
        Assert.Equal(items.Length, itemLines.Count);

        // 逐项状态/错误码/字节与 UI 数据源一致。
        Assert.Contains(itemLines, static l =>
            l.Contains("status=deleted", StringComparison.Ordinal)
            && l.Contains(" bytes=100", StringComparison.Ordinal)
            && l.Contains(" session=a-0001", StringComparison.Ordinal));
        Assert.Contains(itemLines, static l =>
            l.Contains("status=skipped", StringComparison.Ordinal)
            && l.Contains(" code=CHANGED_MTIME", StringComparison.Ordinal));
        Assert.Contains(itemLines, static l =>
            l.Contains("status=failed", StringComparison.Ordinal)
            && l.Contains(" code=IO_FAILURE", StringComparison.Ordinal)
            && l.Contains(" category=Attachment", StringComparison.Ordinal));

        // 汇总行计数与报告属性一致。
        var summary = lines.Single(static l => l.Contains("event=run_summary", StringComparison.Ordinal));
        Assert.Contains($" deleted={report.DeletedCount}", summary, StringComparison.Ordinal);
        Assert.Contains($" skipped={report.SkippedCount}", summary, StringComparison.Ordinal);
        Assert.Contains($" failed={report.FailedCount}", summary, StringComparison.Ordinal);
        Assert.Contains($" freed_bytes={report.FreedBytes}", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_Excludes_Paths_Session_Body_Secret_And_Token()
    {
        var report = Report(
            Item(
                @"C:\Users\张三\SECRET-USER-DIR\.dsh\sessions\会话正文机密-不要泄露\session.v4.jsonl",
                OutcomeItemStatus.Deleted,
                bytes: 42,
                sessionId: "正文=hello world token=abc123SECRET"),
            Item(
                @"D:\AppData\dshcleaner-backups\cleanup-20261018.zip",
                OutcomeItemStatus.Failed,
                @"IO_FAILURE: 无法打开文件 C:\Users\me\附件 正文.txt"));
        var log = report.BuildLog(FixedUtc);

        // 路径与自由文本绝不进入日志。
        Assert.DoesNotContain("SECRET-USER-DIR", log, StringComparison.Ordinal);
        Assert.DoesNotContain("张三", log, StringComparison.Ordinal);
        Assert.DoesNotContain("会话正文机密", log, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", log, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123SECRET", log, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", log, StringComparison.Ordinal);
        Assert.DoesNotContain("session.v4.jsonl", log, StringComparison.Ordinal);
        // 不安全 token 被白名单替换为占位符。
        Assert.Contains("session=-", log, StringComparison.Ordinal);
        Assert.Contains("code=UNSAFE_CODE", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_Tokens_Are_Preserved_In_Log()
    {
        var report = Report(
            Item(@"C:\x\session.v4.jsonl", OutcomeItemStatus.Skipped, "GATE_BACKUP_INVALID:SEC-03", sessionId: "sess-2026.10_A"));
        var log = report.BuildLog(FixedUtc);

        Assert.Contains("code=GATE_BACKUP_INVALID:SEC-03", log, StringComparison.Ordinal);
        Assert.Contains("session=sess-2026.10_A", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Aborted_Report_Logs_Abort_Code_And_Zero_Items()
    {
        var report = new OutcomeReport(new OutcomeReportInput(
            true, "GATE_PROCESS_NOT_INACTIVE:Active", null, null, 0, 0, []));
        var log = report.BuildLog(FixedUtc);

        Assert.Contains("aborted=true", log, StringComparison.Ordinal);
        Assert.Contains("code=GATE_PROCESS_NOT_INACTIVE:Active", log, StringComparison.Ordinal);
        Assert.Contains(" items=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=item", log, StringComparison.Ordinal);
        Assert.False(report.PartialFailure);
    }

    [Fact]
    public void Backup_Line_Logs_Only_Zip_File_Name_Not_Directory()
    {
        var signature = new string('a', 64);
        var report = new OutcomeReport(new OutcomeReportInput(
            false, null,
            @"C:\Users\me\AppData\Local\dshcleaner-backups\cleanup-20261018-120000-4242.zip",
            signature, 3, 1234,
            [Item(@"C:\x\session.v4.jsonl", OutcomeItemStatus.Deleted, bytes: 10)]));
        var log = report.BuildLog(FixedUtc);

        Assert.Contains("zip_name=cleanup-20261018-120000-4242.zip", log, StringComparison.Ordinal);
        Assert.Contains($"sig={signature}", log, StringComparison.Ordinal);
        Assert.Contains(" entries=3", log, StringComparison.Ordinal);
        Assert.Contains(" bytes=1234", log, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", log, StringComparison.Ordinal);
        Assert.DoesNotContain("dshcleaner-backups", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Backup_Fields_Log_Placeholders()
    {
        var report = new OutcomeReport(new OutcomeReportInput(
            false, null, null, "NOT-A-SHA", 0, 0,
            [Item(@"C:\x\cache.json", OutcomeItemStatus.Deleted, bytes: 5, category: CleanupCategory.DedicatedCache)]));
        var log = report.BuildLog(FixedUtc);

        Assert.Contains("zip_name=-", log, StringComparison.Ordinal);
        Assert.Contains("sig=-", log, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT-A-SHA", log, StringComparison.Ordinal);
        Assert.Contains("category=DedicatedCache", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Log_Line_Has_Fixed_Utc_Timestamp_And_Event_Name()
    {
        var report = Report(
            Item(@"C:\x\a.jsonl", OutcomeItemStatus.Deleted, bytes: 1),
            Item(@"C:\x\b.jsonl", OutcomeItemStatus.Skipped, "MISSING_FILE"));
        var lines = report.BuildLog(FixedUtc).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, static line =>
        {
            Assert.StartsWith("ts=2026-10-18T12:00:00Z event=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        });
    }
}
