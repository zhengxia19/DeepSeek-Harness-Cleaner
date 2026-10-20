using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>生成删除清单前重查路径、目录身份、代际集合及扫描快照，冻结逐文件哈希。不执行写入。</summary>
public sealed class SessionCleanupPreparation
{
    public CleanupPlanResult Build(string sessionRoot, IReadOnlyList<SessionSummary> selected)
    {
        var guard = new PathGuard();
        var discovered = new SessionLogDiscovery(guard).Discover(sessionRoot);
        if (!discovered.RootValid) return new CleanupPlanResult(discovered.RootErrorCode, null, []);
        var prepared = new List<SessionSummary>();
        var rejected = new List<PlanExclusion>();
        foreach (var session in selected)
        {
            if (session.Status != ParseStatus.Valid || session.IsAmbiguous || session.SessionId is null)
            {
                prepared.Add(session); // Preserve the planner's specific refusal reason.
                continue;
            }
            try
            {
                var paths = session.Generations.Select(x => Path.GetFullPath(x.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidate = discovered.Candidates.SingleOrDefault(c => c.Files.Any(f => paths.Contains(f.FullPath)));
                if (candidate is null || Path.GetFileName(candidate.FullPath) != session.SessionId
                    || candidate.IsAmbiguous || !paths.SetEquals(candidate.Files.Select(f => f.FullPath))
                    || discovered.Anomalies.Any(a => string.Equals(Path.GetDirectoryName(a.FullPath), candidate.FullPath, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("SESSION_FILE_SET_CHANGED_OR_UNKNOWN");
                var logs = new List<PhysicalLog>();
                foreach (var log in session.Generations)
                {
                    if (!guard.Check(sessionRoot, log.FullPath).IsAllowed) throw new IOException("PATH_REJECTED");
                    var header = new SessionHeaderReader().Read(log.FullPath, session.SessionId);
                    if (log.Generation > SessionHeaderReader.MaxSupportedFormatVersion || !header.AllowsAutoCleanup || header.IdentityMatches != true
                        || (header.Header!.IsNative && header.Header.FormatVersion != log.Generation))
                        throw new IOException("HEADER_CHANGED_OR_UNSUPPORTED");
                    // Opaque extension events can be preserved in a whole-file ZIP; malformed,
                    // truncated or over-limit physical logs must not enter exact cleanup.
                    if (!new SessionEventReader().Read(log.FullPath).IsReadComplete)
                        throw new IOException("LOG_INCOMPLETE_OR_MALFORMED");
                    var info = new FileInfo(log.FullPath);
                    if (info.Length != log.Bytes || info.LastWriteTimeUtc != log.ModifiedAt.UtcDateTime)
                        throw new IOException("SOURCE_CHANGED_SINCE_SCAN");
                    using var input = new FileStream(log.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var sha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                    info.Refresh();
                    if (info.Length != log.Bytes || info.LastWriteTimeUtc != log.ModifiedAt.UtcDateTime)
                        throw new IOException("SOURCE_CHANGED_DURING_HASH");
                    logs.Add(log with { Sha256 = sha });
                }
                prepared.Add(session with { Generations = logs });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                rejected.Add(new PlanExclusion(session.SessionId, "RESCAN_REQUIRED", "文件或身份已变更/无法复验，请重新扫描后重试。"));
            }
        }
        var result = new CleanupPlanner().Build(sessionRoot, prepared, []);
        return result with { Exclusions = result.Exclusions.Concat(rejected).ToArray() };
    }
}
