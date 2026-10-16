using System.IO;

namespace DeepSeekHarnessCleaner.Tests.Fixtures;

/// <summary>
/// 隔离夹具根：在 Path.GetTempPath() 下创建模拟 .dsh 与模拟桌面根，
/// Dispose 时整树删除。绝不指向真实 %USERPROFILE%\.dsh。
/// </summary>
public sealed class FixtureRoot : IDisposable
{
    private static readonly string TempParent = Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures");

    public string Root { get; }
    public string DshHome { get; }
    public string SessionsRoot { get; }
    public string StoragesRoot { get; }
    public string AttachmentsRoot { get; }
    public string DesktopHome { get; }
    public FixtureFactory Factory { get; }
    public bool Disposed { get; private set; }

    private FixtureRoot(string root)
    {
        Root = root;
        DshHome = Path.Combine(root, ".dsh");
        SessionsRoot = Path.Combine(DshHome, "sessions");
        StoragesRoot = Path.Combine(DshHome, "storages");
        AttachmentsRoot = Path.Combine(DshHome, "attachments");
        DesktopHome = Path.Combine(root, "@deepseek-ai", "dsh-desktop");
        Factory = new FixtureFactory(this);
    }

    public static FixtureRoot Create()
    {
        Directory.CreateDirectory(TempParent);
        var fixture = new FixtureRoot(Path.Combine(TempParent, "fx-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture.SessionsRoot);
        Directory.CreateDirectory(fixture.StoragesRoot);
        Directory.CreateDirectory(fixture.AttachmentsRoot);
        Directory.CreateDirectory(fixture.DesktopHome);
        return fixture;
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        Disposed = true;
    }
}
