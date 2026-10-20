using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure.Compression;

namespace DeepSeekHarnessCleaner.Infrastructure;

public sealed record AttachmentInventoryItem(AttachmentCandidate Candidate, string RelativePath,
    string? AttachmentId, IReadOnlyList<string> ReferencingSessionIds)
{
    public string DisplayName { get; init; } = "名称未知";
    public string TypeLabel { get; init; } = "类型未知";
}

public sealed record AttachmentInventoryResult(IReadOnlyList<AttachmentInventoryItem> Items,
    IReadOnlyList<string> Issues);

/// <summary>受控根内附件只读盘点。引用只按已核对的原生内容块建立；不提供全局孤儿结论。</summary>
public sealed class AttachmentInventoryService
{
    private const int MaxReferenceReadBytes = 32 * 1024 * 1024;

    public AttachmentInventoryResult Scan(string dataRoot, IReadOnlyList<SessionSummary> sessions, CancellationToken ct = default)
    {
        var guard = new PathGuard();
        var attachmentRoot = Path.Combine(dataRoot, "attachments");
        var issues = new List<string>();
        var files = new List<(AttachmentCandidate Candidate, string Relative, string? Id)>();
        Walk(attachmentRoot);

        var sources = new List<SessionAttachmentSource>();
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (session.SessionId is null || session.IsAmbiguous) continue;
            foreach (var log in session.Generations)
            {
                ct.ThrowIfCancellationRequested();
                string? text = null;
                try
                {
                    if (!guard.Check(Path.Combine(dataRoot, "sessions"), log.FullPath).IsAllowed)
                        throw new IOException("REFERENCE_PATH_REJECTED");
                    var header = new SessionHeaderReader().Read(log.FullPath, session.SessionId);
                    if (header.IdentityMatches != true || header.Header is null)
                        throw new IOException("REFERENCE_IDENTITY_CHANGED");
                    using var input = new FileStream(log.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (log.Compressed)
                    {
                        var decoded = ZstdFrameDecoder.Decode(input, MaxReferenceReadBytes, cancellationToken: ct);
                        if (decoded.IsSuccess) text = Encoding.UTF8.GetString(decoded.Content);
                        else issues.Add("REFERENCE_DECODE_INCOMPLETE");
                    }
                    else if (input.Length <= MaxReferenceReadBytes)
                    {
                        using var reader = new StreamReader(input, Encoding.UTF8);
                        text = reader.ReadToEnd();
                    }
                    else issues.Add("REFERENCE_READ_LIMIT");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add("REFERENCE_UNREADABLE");
                }
                // Even a complete physical log does not prove all external/old/plugin references are covered.
                // Positive native references are shown; absence always remains Unknown.
                sources.Add(new SessionAttachmentSource(session.SessionId, text, ParseStatus.Partial));
            }
        }
        var index = new AttachmentIndex().Build(sources, files.Where(f => f.Id is not null).Select(f => f.Id!).ToArray());
        var byId = index.Attachments.ToDictionary(x => x.AttachmentId, StringComparer.Ordinal);
        var items = files.Select(f =>
        {
            var evidence = f.Id is not null && byId.TryGetValue(f.Id, out var found) ? found : null;
            return new AttachmentInventoryItem(f.Candidate with { EvidenceStatus = evidence?.Status ?? AttachmentEvidenceStatus.Unknown },
                f.Relative, f.Id, evidence?.ReferencingSessionIds ?? [])
            {
                DisplayName = evidence is { DisplayNames.Count: > 0 } ? string.Join("；", evidence.DisplayNames)
                    : f.Id is null && !IsDigest(Path.GetFileName(f.Relative)) ? Path.GetFileName(f.Relative) + "（存储文件名）" : "名称未知",
                TypeLabel = evidence is { TypeLabels.Count: > 0 } ? string.Join("；", evidence.TypeLabels) : "类型未知",
            };
        }).OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        return new AttachmentInventoryResult(items, issues);

        void Walk(string directory)
        {
            ct.ThrowIfCancellationRequested();
            var directoryCheck = guard.Check(dataRoot, directory, allowDirectory: true);
            if (!directoryCheck.IsAllowed) { issues.Add(directoryCheck.ErrorCode ?? "ATTACHMENT_ROOT_MISSING"); return; }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var check = guard.Check(dataRoot, path, allowDirectory: true);
                    if (!check.IsAllowed) { issues.Add(check.ErrorCode ?? "ATTACHMENT_PATH_REJECTED"); continue; }
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0) { Walk(path); continue; }
                    var info = new FileInfo(path);
                    var length = info.Length;
                    var modified = info.LastWriteTimeUtc;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    info.Refresh();
                    if (length != info.Length || modified != info.LastWriteTimeUtc) { issues.Add("ATTACHMENT_CHANGED"); continue; }
                    var relative = Path.GetRelativePath(attachmentRoot, path);
                    var segments = relative.Split(Path.DirectorySeparatorChar);
                    string? id = null;
                    if (segments.Length == 4 && segments[0] == "v1" && segments[1] == "objects"
                        && segments[3] == sha && segments[2] == sha[..2]) id = "sha256:" + sha;
                    if (segments.Length == 4 && segments[0] == "v1" && segments[1] == "file-objects"
                        && segments[3] == sha && segments[2] == sha[..2]) id = "sha256:" + sha;
                    if (segments.Length == 5 && segments[0] == "v1" && segments[1] == "files"
                        && segments[3] == sha && segments[2] == sha[..2]) id = "sha256:" + sha;
                    files.Add((new AttachmentCandidate(path, AttachmentEvidenceStatus.Unknown, true, length, modified, sha), relative, id));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add("ATTACHMENT_SCAN_INCOMPLETE");
            }
        }
    }

    private static bool IsDigest(string name) => name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
