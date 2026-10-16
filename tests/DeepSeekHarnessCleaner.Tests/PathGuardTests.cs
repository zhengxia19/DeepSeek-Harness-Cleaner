using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Infrastructure;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>TC-15/TC-20：路径白名单验证——..、junction、符号链接、重命名、UNC/设备/ADS/根类均被拒绝，哨兵 SHA 不变。</summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _root;
    private readonly PathGuard _guard = new();

    public PathGuardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dshcleaner-pathguard-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempRoot, "sessionroot");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "outside"));
    }

    public void Dispose()
    {
        try
        {
            // Directory.Delete(recursive) 无法删除 junction：先移除 reparse 链接本身。
            foreach (var dir in Directory.EnumerateDirectories(_tempRoot, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(dir, recursive: false);
            }
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateFile(string dir, string name, string content = "x")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>用 mklink /J 创建 junction（无需管理员权限）。</summary>
    private static void CreateJunction(string linkPath, string targetDirectory)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDirectory}\"")
        {
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        if (!process.WaitForExit(15_000) || process.ExitCode != 0 || !Directory.Exists(linkPath))
            throw new InvalidOperationException("mklink /J 创建 junction 失败：" + linkPath);
    }

    [Fact]
    public void NormalFileInsideRoot_IsAllowed()
    {
        var file = CreateFile(Path.Combine(_root, "sub"), "session.jsonl");
        var result = _guard.Check(_root, file);
        Assert.True(result.IsAllowed);
        Assert.Equal(Path.Combine("sub", "session.jsonl"), result.RelativePath);
    }

    [Fact]
    public void DotDotEscape_IsRejected()
    {
        var outside = CreateFile(_tempRoot, "outside.txt");
        var result = _guard.Check(_root, Path.Combine(_root, "..", "outside.txt"));
        Assert.Equal(PathCheckStatus.Rejected, result.Status);
        Assert.Equal(PathGuard.PathEscapeErrorCode, result.ErrorCode);
        Assert.Equal(PathCheckStatus.Rejected, _guard.Check(_root, outside).Status);
    }

    [Fact]
    public void DotDotResolvingInsideRoot_StillRejected()
    {
        var file = CreateFile(Path.Combine(_root, "sub"), "session.jsonl");
        var sneaky = Path.Combine(_root, "sub", "..", "sub", "session.jsonl");
        var result = _guard.Check(_root, sneaky);
        Assert.Equal(PathCheckStatus.Rejected, result.Status);
        Assert.Equal(PathGuard.PathEscapeErrorCode, result.ErrorCode);
    }

    [Fact]
    public void JunctionToOutsideDirectory_IsRejected_AndSentinelShaUnchanged()
    {
        var sentinel = CreateFile(Path.Combine(_tempRoot, "outside"), "sentinel.txt", "sentinel-content");
        var before = Sha256(sentinel);
        CreateJunction(Path.Combine(_root, "link"), Path.Combine(_tempRoot, "outside"));

        var result = _guard.Check(_root, Path.Combine(_root, "link", "sentinel.txt"));

        Assert.Equal(PathCheckStatus.Rejected, result.Status);
        Assert.Equal(PathGuard.PathReparseErrorCode, result.ErrorCode);
        Assert.Equal(before, Sha256(sentinel)); // SEC-01：根外哨兵 hash 不变
    }

    [Fact]
    public void JunctionAsRoot_IsRejected()
    {
        var junction = Path.Combine(_tempRoot, "root-junction");
        CreateJunction(junction, _root);
        var file = CreateFile(_root, "session.jsonl");

        var result = _guard.Check(junction, file);
        Assert.Equal(PathCheckStatus.Rejected, result.Status);
        Assert.Equal(PathGuard.PathReparseErrorCode, result.ErrorCode);
    }

    [Fact]
    public void SymbolicLinkFile_IsRejected()
    {
        var real = CreateFile(_tempRoot, "real.txt");
        var link = Path.Combine(_root, "session.jsonl");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // 无符号链接权限（非开发者模式）：跳过，junction 用例已覆盖 reparse
        }

        var result = _guard.Check(_root, link);
        Assert.Equal(PathCheckStatus.Rejected, result.Status);
        Assert.Equal(PathGuard.PathReparseErrorCode, result.ErrorCode);
    }

    [Fact]
    public void RenamedPath_IsMissing_NotAllowed()
    {
        var file = CreateFile(_root, "session.jsonl");
        var result1 = _guard.Check(_root, file);
        Assert.True(result1.IsAllowed);

        File.Move(file, Path.Combine(_root, "renamed.jsonl"));
        var result2 = _guard.Check(_root, file);
        Assert.Equal(PathCheckStatus.Missing, result2.Status);
        Assert.False(result2.IsAllowed);
    }

    [Fact]
    public void UncAndDeviceAndAdsPaths_AreRejected()
    {
        Assert.Equal(PathGuard.PathEscapeErrorCode,
            _guard.Check(_root, @"\\localhost\c$\x\session.jsonl").ErrorCode);
        Assert.Equal(PathGuard.PathEscapeErrorCode,
            _guard.Check(_root, @"\\?\C:\Windows\session.jsonl").ErrorCode);
        var file = CreateFile(_root, "ads.txt");
        Assert.Equal(PathGuard.PathEscapeErrorCode,
            _guard.Check(_root, file + ":stream").ErrorCode);
    }

    [Fact]
    public void DriveRoot_UserProfileRoot_ProjectDirectory_AreInvalidRoots()
    {
        var file = CreateFile(_root, "session.jsonl");
        Assert.Equal(PathGuard.RootInvalidErrorCode, _guard.Check(@"C:\", file).ErrorCode);
        Assert.Equal(PathGuard.RootInvalidErrorCode,
            _guard.Check(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file).ErrorCode);
        Assert.Equal(PathGuard.RootInvalidErrorCode, _guard.Check(AppContext.BaseDirectory, file).ErrorCode);
        Assert.Equal(PathGuard.RootInvalidErrorCode, _guard.Check(Path.Combine(_tempRoot, "no-such-root"), file).ErrorCode);
    }

    [Fact]
    public void RootItself_AndDirectoryTarget_AreRejected()
    {
        Assert.Equal(PathCheckStatus.Rejected, _guard.Check(_root, _root).Status);
        var dir = Path.Combine(_root, "sub");
        Assert.Equal(PathGuard.PathEscapeErrorCode, _guard.Check(_root, dir).ErrorCode);
        Assert.True(_guard.Check(_root, dir, allowDirectory: true).IsAllowed);
    }

    [Fact]
    public void Containment_IsCaseInsensitiveOnWindows()
    {
        var file = CreateFile(Path.Combine(_root, "sub"), "session.jsonl");
        var upper = file.ToUpperInvariant();
        Assert.True(_guard.Check(_root.ToUpperInvariant(), upper).IsAllowed);
    }

    [Fact]
    public void SessionFileNameWhitelist_MatchesDesignContract()
    {
        Assert.True(_guard.IsSessionFileName("session.jsonl"));
        Assert.True(_guard.IsSessionFileName("session.jsonl.zstd"));
        Assert.True(_guard.IsSessionFileName("session.v4.jsonl"));
        Assert.True(_guard.IsSessionFileName("session.v10.jsonl.zstd"));
        Assert.False(_guard.IsSessionFileName("Session.jsonl"));      // 大写不自动删（design.md §3）
        Assert.False(_guard.IsSessionFileName("session.json"));
        Assert.False(_guard.IsSessionFileName("session.jsonl.bak"));
        Assert.False(_guard.IsSessionFileName("session.v0.jsonl"));   // N 为正整数
        Assert.False(_guard.IsSessionFileName("session.vA.jsonl"));
        Assert.False(_guard.IsSessionFileName("other.jsonl"));
    }

    [Fact]
    public void DesktopCacheWhitelist_ExcludesBrowserStateDirs()
    {
        Assert.True(_guard.IsDesktopCacheDirectory("Cache"));
        Assert.True(_guard.IsDesktopCacheDirectory(Path.Combine("Code Cache", "f_001")));
        Assert.True(_guard.IsDesktopCacheDirectory("GPUCache"));
        Assert.True(_guard.IsDesktopCacheDirectory("DawnGraphiteCache"));
        Assert.True(_guard.IsDesktopCacheDirectory("DawnWebGPUCache"));
        Assert.False(_guard.IsDesktopCacheDirectory("Local Storage"));
        Assert.False(_guard.IsDesktopCacheDirectory("Session Storage"));
        Assert.False(_guard.IsDesktopCacheDirectory("Network"));
        Assert.False(_guard.IsDesktopCacheDirectory("logs"));
    }
}
