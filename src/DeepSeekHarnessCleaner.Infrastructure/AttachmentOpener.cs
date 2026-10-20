using System.Diagnostics;
using System.Security.Cryptography;

namespace DeepSeekHarnessCleaner.Infrastructure;

public sealed record AttachmentOpenResult(bool Success, string? OpenedPath, string? Error);

/// <summary>Explicit local-file opening. Never renames or writes the source attachment.</summary>
public sealed class AttachmentOpener
{
    private readonly Action<string> _launch;
    private readonly string _copyRoot;

    public AttachmentOpener(Action<string>? launch = null, string? copyRoot = null)
    {
        _launch = launch ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" }));
        _copyRoot = Path.GetFullPath(copyRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarnessCleaner", "opened-attachments"));
    }

    public AttachmentOpenResult Open(string dataHome, AttachmentInventoryItem item, CancellationToken ct = default)
    {
        string? copy = null;
        try
        {
            var guard = new PathGuard();
            var source = item.Candidate;
            var attachmentRoot = Path.Combine(dataHome, "attachments");
            if (!guard.Check(attachmentRoot, source.FullPath).IsAllowed) throw new IOException("附件路径失效或不安全，请刷新。");
            using (var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(source.FullPath);
                if (input.Length != source.ExpectedLength || info.LastWriteTimeUtc != source.ExpectedWriteUtc.UtcDateTime)
                    throw new IOException("附件已变化，请刷新。");
                var sha = Hash(input, ct);
                if (!string.Equals(sha, source.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("附件摘要已变化，请刷新。");
                input.Position = 0;
                if (string.IsNullOrEmpty(Path.GetExtension(source.FullPath)))
                {
                    Span<byte> header = stackalloc byte[16];
                    var read = input.Read(header);
                    var extension = DetectExtension(header[..read]);
                    if (extension is null) throw new IOException("附件无扩展名且无法确认类型，不能确定默认应用。");
                    EnsureSafeCopyRoot();
                    copy = Path.Combine(_copyRoot, Guid.NewGuid().ToString("N") + extension);
                    input.Position = 0;
                    using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int count;
                        while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                    }
                    using var verify = File.OpenRead(copy);
                    if (verify.Length != source.ExpectedLength || !string.Equals(Hash(verify, ct), sha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("打开副本校验失败。");
                }
            }
            ct.ThrowIfCancellationRequested();
            if (!guard.Check(attachmentRoot, item.Candidate.FullPath).IsAllowed) throw new IOException("附件路径已变化。");
            if (copy is not null && !guard.Check(_copyRoot, copy).IsAllowed) throw new IOException("打开副本路径已变化。");
            var opened = copy ?? item.Candidate.FullPath;
            _launch(opened);
            return new AttachmentOpenResult(true, opened, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or OperationCanceledException or System.Security.SecurityException)
        {
            if (copy is not null && new PathGuard().Check(_copyRoot, copy).IsAllowed)
            {
                try { File.Delete(copy); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            return new AttachmentOpenResult(false, null, ex is OperationCanceledException ? "打开已取消。" : ex.Message);
        }
    }

    private static string Hash(Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void EnsureSafeCopyRoot()
    {
        // Check every existing ancestor before creating a tool-owned directory.
        for (var current = new DirectoryInfo(_copyRoot); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("打开副本目录包含重解析点。");
        Directory.CreateDirectory(_copyRoot);
        for (var current = new DirectoryInfo(_copyRoot); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("打开副本目录包含重解析点。");
    }

    private static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ".gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (bytes.StartsWith("%PDF-"u8)) return ".pdf";
        return null;
    }
}
