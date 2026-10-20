using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>日期筛选所依据的会话时间字段（design.md §3 筛选模式）。</summary>
public enum DateFilterField
{
    /// <summary>有效 header 的 createdAt。</summary>
    CreatedAt,

    /// <summary>经 schema 验证的最近活动事件时间（缺失即未知）。</summary>
    LastActivityAt,

    /// <summary>物理文件 LastWriteTimeUtc 兜底（UI 须标注“文件修改时间（非会话活动）”）。</summary>
    FileModified,
}

/// <summary>包含式日期区间 [Start, End]，按指定时区的本地日期比较（design.md §3）。</summary>
public sealed record DateFilter(DateFilterField Field, DateOnly Start, DateOnly End);

/// <summary>会话筛选请求；null 或空白条件表示不限制该维度，各条件按 AND 组合。</summary>
public sealed record FilterRequest(
    DateFilter? Date,
    string? Project,
    string? SessionId,
    string? Keyword);

/// <summary>
/// T-11 会话日期与搜索筛选（TC-07/TC-08）。筛选只按完整会话粒度取舍，
/// 绝不裁剪会话内事件内容；未知日期不参与日期区间，也不硬填为今天。
/// </summary>
public static class SessionFilter
{
    /// <summary>
    /// 应用筛选。日期区间以 <paramref name="timeZone"/> 本地日历日包含式比较
    /// （等价于 [start 00:00, day-after-end 00:00) 瞬时区间）；
    /// 关键字对项目名与会话 ID 做大小写不敏感包含匹配（事件正文关键字属事件流任务）。
    /// </summary>
    public static IReadOnlyList<SessionSummary> Apply(
        IReadOnlyList<SessionSummary> sessions,
        FilterRequest request,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (request.Date is { } range && range.Start > range.End)
            throw new ArgumentException("日期区间 Start 不得晚于 End。", nameof(request));

        var project = Normalize(request.Project);
        var sessionId = Normalize(request.SessionId);
        var keyword = Normalize(request.Keyword);

        var result = new List<SessionSummary>();
        foreach (var session in sessions)
        {
            if (request.Date is { } dateRange && !MatchesDate(session, dateRange, timeZone))
                continue;
            if (project is not null && !(session.Project?.Contains(project, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            if (sessionId is not null && !(session.SessionId?.Contains(sessionId, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            if (keyword is not null && !MatchesKeyword(session, keyword))
                continue;
            result.Add(session);
        }

        return result;
    }

    private static bool MatchesDate(SessionSummary session, DateFilter range, TimeZoneInfo timeZone)
    {
        var instant = range.Field switch
        {
            DateFilterField.CreatedAt => session.CreatedAt,
            DateFilterField.LastActivityAt => session.LastActivityAt,
            DateFilterField.FileModified => MaxModifiedAt(session),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range.Field, "未知日期筛选字段。"),
        };

        // 未知日期不参与区间匹配（不硬填为今天）。
        if (instant is null)
            return false;

        var local = instant.Value.ToOffset(timeZone.GetUtcOffset(instant.Value));
        var day = DateOnly.FromDateTime(local.DateTime);
        return day >= range.Start && day <= range.End;
    }

    private static DateTimeOffset? MaxModifiedAt(SessionSummary session)
    {
        DateTimeOffset? max = null;
        foreach (var log in session.Generations)
        {
            if (max is null || log.ModifiedAt > max)
                max = log.ModifiedAt;
        }

        return max;
    }

    private static bool MatchesKeyword(SessionSummary session, string keyword) =>
        (session.Project is not null && session.Project.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        || (session.SessionId is not null && session.SessionId.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
