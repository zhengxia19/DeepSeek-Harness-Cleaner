using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessCleaner.Application;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>已校验发布的备份事实（T-22）。Signature 为 manifest 清单签名。</summary>
public sealed record VerifiedBackup(
    string ZipPath,
    string ManifestSignature,
    int FileEntryCount,
    long TotalBytes);

/// <summary>备份结果。失败时 ErrorCode/Message 可解释，Backup 为 null。</summary>
public sealed record BackupResult(bool Success, string? ErrorCode, string? Message, VerifiedBackup? Backup)
{
    public static BackupResult Fail(string code, string message) => new(false, code, message, null);

    public static BackupResult Ok(VerifiedBackup backup) => new(true, null, null, backup);
}

/// <summary>
/// 原始 ZIP 备份创建与校验（T-22，interfaces.md §ZIP 导出契约，TC-09/TC-20）。
/// 流程：按 manifest 流式写同目录临时 zip（条目原字节 + manifest.json）→
/// 重新打开 zip 校验条目数量、逐条目解压字节 SHA-256 与长度、manifest.json 签名一致 →
/// 无覆盖发布（目标已存在即拒绝，File.Move 原子改名）。
/// 任何失败（损坏、IO/磁盘满、取消、校验不符）都移除临时 zip，绝不返回成功、绝不触碰旧备份。
/// </summary>
public sealed class BackupService
{
    public const string ManifestEntryName = "manifest.json";

    private const string CodeTargetExists = "TARGET_EXISTS";
    private const string CodeCanceled = "CANCELED";
    private const string CodeIo = "IO_FAILURE";
    private const string CodeVerify = "ZIP_VERIFY_FAILED";
    private const string CodeSchema = "UNSUPPORTED_SCHEMA";

    /// <summary>创建并校验备份 zip。<paramref name="targetZipPath"/> 所在目录必须已存在；目标已存在时拒绝且不覆盖。</summary>
    public BackupResult CreateBackup(BackupManifest manifest, string targetZipPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetZipPath);

        if (manifest.SchemaVersion != BackupManifest.CurrentSchemaVersion)
        {
            return BackupResult.Fail(CodeSchema, $"不支持的 manifest schemaVersion={manifest.SchemaVersion}");
        }

        string? tempPath = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var targetFull = Path.GetFullPath(targetZipPath);
            var directory = Path.GetDirectoryName(targetFull);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return BackupResult.Fail(CodeIo, "目标目录不存在");
            }

            if (File.Exists(targetFull))
            {
                return BackupResult.Fail(CodeTargetExists, "目标备份文件已存在，拒绝覆盖");
            }

            tempPath = targetFull + ".tmp-" + Guid.NewGuid().ToString("N");
            WriteZip(manifest, tempPath, ct);

            var verifyMessage = VerifyZipContent(tempPath, manifest);
            if (verifyMessage is not null)
            {
                TryDeleteTemp(tempPath);
                return BackupResult.Fail(CodeVerify, verifyMessage);
            }

            // 发布前复验：目标出现（竞态/旧备份）即放弃，不覆盖。
            if (File.Exists(targetFull))
            {
                TryDeleteTemp(tempPath);
                return BackupResult.Fail(CodeTargetExists, "发布前目标已存在，拒绝覆盖");
            }

            File.Move(tempPath, targetFull, overwrite: false);
            tempPath = null;

            return BackupResult.Ok(new VerifiedBackup(
                targetFull,
                BackupManifestBuilder.ComputeSignature(manifest),
                manifest.Entries.Count,
                manifest.Entries.Sum(static e => e.Length)));
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemp(tempPath);
            return BackupResult.Fail(CodeCanceled, "进程取消，临时 zip 已移除");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            return BackupResult.Fail(CodeIo, $"IO 失败：{ex.GetType().Name}");
        }
    }

    /// <summary>对已发布 zip 复验（SEC-03：单字节破坏必须失败）。不修改任何文件。</summary>
    public BackupResult VerifyZip(string zipPath, BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        try
        {
            var full = Path.GetFullPath(zipPath);
            if (!File.Exists(full))
            {
                return BackupResult.Fail(CodeVerify, "zip 不存在");
            }

            var message = VerifyZipContent(full, manifest);
            if (message is not null)
            {
                return BackupResult.Fail(CodeVerify, message);
            }

            return BackupResult.Ok(new VerifiedBackup(
                full,
                BackupManifestBuilder.ComputeSignature(manifest),
                manifest.Entries.Count,
                manifest.Entries.Sum(static e => e.Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return BackupResult.Fail(CodeVerify, $"zip 不可读：{ex.GetType().Name}");
        }
    }

    // ---- 内部 ----

    private static void WriteZip(BackupManifest manifest, string tempPath, CancellationToken ct)
    {
        using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var ms = manifestEntry.Open())
            {
                var json = Encoding.UTF8.GetBytes(BackupManifestBuilder.ToJson(manifest));
                ms.Write(json, 0, json.Length);
            }

            foreach (var entry in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.Combine(manifest.Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var dataEntry = zip.CreateEntry(entry.ZipPath, CompressionLevel.Optimal);
                using var entryStream = dataEntry.Open();
                using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
                source.CopyTo(entryStream);
            }
        }
    }

    /// <summary>返回 null 表示通过；否则为可解释的失败原因。</summary>
    private static string? VerifyZipContent(string zipPath, BackupManifest manifest)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(fs, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            return $"zip 结构损坏：{ex.Message}";
        }

        using (zip)
        {
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!byName.TryAdd(entry.FullName, entry))
                {
                    return $"zip 内重复条目：{entry.FullName}";
                }
            }

            if (zip.Entries.Count != manifest.Entries.Count + 1)
            {
                return $"条目数量不符：zip={zip.Entries.Count}，期望={manifest.Entries.Count + 1}";
            }

            if (!byName.TryGetValue(ManifestEntryName, out var manifestEntry))
            {
                return "缺少 manifest.json 条目";
            }

            string manifestJson;
            try
            {
                using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                manifestJson = reader.ReadToEnd();
            }
            catch (InvalidDataException ex)
            {
                return $"manifest.json 条目损坏：{ex.Message}";
            }

            var loaded = BackupManifestBuilder.FromJson(manifestJson);
            if (!loaded.Success)
            {
                return "manifest.json 内容非法";
            }

            if (BackupManifestBuilder.ComputeSignature(loaded.Manifest!) != BackupManifestBuilder.ComputeSignature(manifest))
            {
                return "manifest.json 签名与预期清单不一致";
            }

            foreach (var expected in manifest.Entries)
            {
                if (!byName.TryGetValue(expected.ZipPath, out var actual))
                {
                    return $"缺少条目：{expected.ZipPath}";
                }

                if (actual.Length != expected.Length)
                {
                    return $"条目长度不符：{expected.ZipPath}（zip={actual.Length}，期望={expected.Length}）";
                }

                string sha;
                try
                {
                    using var entryStream = actual.Open();
                    sha = Convert.ToHexString(SHA256.HashData(entryStream)).ToLowerInvariant();
                }
                catch (InvalidDataException ex)
                {
                    return $"条目数据损坏：{expected.ZipPath}（{ex.Message}）";
                }

                if (!string.Equals(sha, expected.Sha256, StringComparison.Ordinal))
                {
                    return $"条目 SHA-256 不符：{expected.ZipPath}";
                }
            }

            return null;
        }
    }

    private static void TryDeleteTemp(string? tempPath)
    {
        if (tempPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件删除失败不掩盖原始失败原因；目录内残留由调用方可见。
        }
    }
}
