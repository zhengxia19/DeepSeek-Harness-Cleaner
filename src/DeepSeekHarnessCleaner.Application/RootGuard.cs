using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessCleaner.Application;

/// <summary>
/// 根契约单一实现（S02）：根规范化、根内复验、根指纹。收敛此前
/// CleanupPlanner/BackupManifest/Cache/Desktop/Attachment/CleanupExecutor 的发散拷贝。
/// 统一语义取最严一套，理由：驱动器根与 UNC 共享根（规范化后恰等于其自身路径根）
/// 作为清理根会波及整卷/整共享，是真实威胁；此前仅 Cache/Desktop/Attachment 变体拒绝，
/// 对既有合法根（数据根均为驱动器根下的子目录）统一取严不改变任何有效输入。
/// Trim 容忍调用方附带空白；规范化去尾分隔符保证同根指纹稳定（原 CleanupExecutor
/// 拷贝不做 TrimEnd，根带尾分隔符时与规划器指纹不一致而误判 ROOT_CHANGED）。
/// 根指纹为规范化根完整路径（UTF-8）SHA-256 小写 hex，仅在单次操作内比对，不跨运行依赖格式。
/// </summary>
internal static class RootGuard
{
    public const string CodeInvalid = "INVALID";
    public const string CodeTraversal = "PATH_TRAVERSAL";
    public const string CodeCrossRoot = "CROSS_ROOT";

    /// <summary>根规范化：必须为完全限定路径，拒驱动器根/UNC 共享根，去尾分隔符。非法抛 <see cref="ArgumentException"/>。</summary>
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root.Trim());
        if (!Path.IsPathFullyQualified(full))
        {
            throw new ArgumentException("root 必须是绝对路径", nameof(root));
        }

        var rootPath = Path.GetPathRoot(full);
        if (rootPath is not { Length: > 1 }
            || string.Equals(rootPath, full, StringComparison.OrdinalIgnoreCase))
        {
            // 相对或驱动器根、UNC 共享根：不得作为清理根。
            throw new ArgumentException("根不得为驱动器根", nameof(root));
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 根内复验：原始路径任何 .. 段先于规范化拒绝（zip-slip 特征，不依赖 GetFullPath 折叠语义）；
    /// 根外/跨盘拒 CROSS_ROOT；空白或不可解析拒 INVALID。成功返回 null 并给出规范化完整路径。
    /// </summary>
    public static string? TryContain(string rootFull, string candidatePath, out string full)
    {
        full = candidatePath;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return CodeInvalid;
        }

        foreach (var segment in candidatePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..")
            {
                return CodeTraversal;
            }
        }

        try
        {
            full = Path.GetFullPath(candidatePath);
        }
        catch (ArgumentException)
        {
            return CodeInvalid;
        }

        if (!IsUnderRoot(rootFull, full))
        {
            return CodeCrossRoot;
        }

        return null;
    }

    /// <summary>已规范化完整路径是否位于规范化根内（根外/跨盘为假）。</summary>
    public static bool IsUnderRoot(string rootFull, string full)
    {
        var relative = Path.GetRelativePath(rootFull, full);
        return !Path.IsPathFullyQualified(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>根指纹：规范化根完整路径（UTF-8）SHA-256 小写 hex；同根稳定，异根不同。</summary>
    public static string RootFingerprint(string root) => FingerprintOf(NormalizeRoot(root));

    /// <summary>根非法时返回 null，供以“不匹配即拒绝”方式比对计划指纹的调用方使用。</summary>
    public static string? TryRootFingerprint(string root)
    {
        try
        {
            return FingerprintOf(NormalizeRoot(root));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>对已规范化根直接计算指纹，避免调用方重复规范化。</summary>
    public static string FingerprintOf(string normalizedRootFull) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRootFull))).ToLowerInvariant();
}
