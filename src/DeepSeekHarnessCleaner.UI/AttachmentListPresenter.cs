using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.UI;

/// <summary>附件列表一行：磁盘事实（ID=文件名、大小）+ AttachmentIndex 引用证据（N-06，只读展示）。</summary>
public sealed record AttachmentRow(
    string AttachmentId,
    string FullPath,
    long SizeBytes,
    AttachmentEvidenceStatus Status,
    IReadOnlyList<string> ReferencingSessionIds);

/// <summary>
/// 附件列表构建结果。<see cref="AttachmentsRootError"/>：null=正常枚举；
/// "MISSING"=数据根下无 attachments 目录（列表为空，但悬空引用仍列出）。
/// </summary>
public sealed record AttachmentListOutcome(
    IReadOnlyList<AttachmentRow> Rows,
    IReadOnlyList<DanglingAttachmentReference> DanglingReferences,
    bool GlobalOrphanConclusionAvailable,
    IReadOnlyList<string> UnresolvedSessionIds,
    string? AttachmentsRootError);

/// <summary>
/// N-06 附件列表演示器（只读，不写不删）：在数据根 <c>attachments/</c> 下经 PathGuard 白名单
/// 枚举普通文件（附件 ID = 文件名，同名保留首个），解码全部会话的预览代际文本
/// （与预览同一代际选择与读取上限，不可读计为未解析），再经 AttachmentIndex.Build 建立引用证据。
/// 后台线程执行、可取消；判定逻辑完全复用 AttachmentIndex（T-15），不改动。
/// </summary>
public sealed class AttachmentListPresenter
{
    private readonly PathGuard _pathGuard = new();

    public Task<AttachmentListOutcome> BuildAsync(
        string dataHome,
        IReadOnlyList<SessionSummary> sessions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataHome);
        ArgumentNullException.ThrowIfNull(sessions);
        return Task.Run(() => Build(dataHome, sessions, ct), ct);
    }

    private AttachmentListOutcome Build(string dataHome, IReadOnlyList<SessionSummary> sessions, CancellationToken ct)
    {
        var attachmentsRoot = Path.Combine(dataHome, AttachmentCleanupSelection.AttachmentsDirName);
        var filesById = new Dictionary<string, (string FullPath, long Size)>(StringComparer.Ordinal);
        string? rootError = null;

        if (Directory.Exists(attachmentsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(attachmentsRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var check = _pathGuard.Check(dataHome, file, allowDirectory: false);
                if (!check.IsAllowed)
                {
                    // 白名单外/重解析点：不进入列表（只读展示，不报错、不跟随）。
                    continue;
                }

                var attributes = File.GetAttributes(check.FullPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    continue;
                }

                var id = Path.GetFileName(check.FullPath);
                if (string.IsNullOrWhiteSpace(id) || filesById.ContainsKey(id))
                {
                    // 同名 blob 保留首个枚举项；AttachmentIndex 本身也按 ID 去重。
                    continue;
                }

                filesById[id] = (check.FullPath, new FileInfo(check.FullPath).Length);
            }
        }
        else
        {
            rootError = "MISSING";
        }

        var sources = new List<SessionAttachmentSource>(sessions.Count);
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var transcript = ReadSessionTranscript(dataHome, session, ct);
            sources.Add(new SessionAttachmentSource(session.SessionId ?? "(未知会话)", transcript, session.Status));
        }

        var index = new AttachmentIndex().Build(sources, filesById.Keys.ToList());
        var rows = new List<AttachmentRow>(index.Attachments.Count);
        foreach (var evidence in index.Attachments)
        {
            if (filesById.TryGetValue(evidence.AttachmentId, out var file))
            {
                rows.Add(new AttachmentRow(
                    evidence.AttachmentId,
                    file.FullPath,
                    file.Size,
                    evidence.Status,
                    evidence.ReferencingSessionIds));
            }
        }

        return new AttachmentListOutcome(
            rows,
            index.DanglingReferences,
            index.GlobalOrphanConclusionAvailable,
            index.UnresolvedSessionIds,
            rootError);
    }

    /// <summary>与预览同一代际选择与读取上限解码转录文本；不可读返回 null（AttachmentIndex 计为未解析会话）。</summary>
    private string? ReadSessionTranscript(string dataHome, SessionSummary session, CancellationToken ct)
    {
        var selected = LegacyPreview.SelectPreviewGeneration(session);
        var log = selected is null
            ? null
            : session.Generations.FirstOrDefault(g => g.Generation == selected.Value);
        if (log is null)
        {
            return null;
        }

        var check = _pathGuard.Check(dataHome, log.FullPath, allowDirectory: false);
        if (!check.IsAllowed)
        {
            return null;
        }

        var warnings = new List<string>();
        return PreviewPresenter.ReadTranscript(check.FullPath, log.Compressed, warnings, ct);
    }
}
