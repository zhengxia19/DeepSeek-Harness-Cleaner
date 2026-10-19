using System.Drawing;
using DeepSeekHarnessCleaner.Infrastructure;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>
/// N-03 窗口几何恢复纯函数（design.md §9）：跨屏校验与全出屏回退判定。
/// 不依赖真实屏幕：工作区矩形由测试注入。
/// </summary>
public sealed class WindowGeometryTests
{
    private static readonly Rectangle[] SingleScreen = [new Rectangle(0, 0, 1920, 1040)];

    [Fact]
    public void NoSavedPosition_DoesNotResolve()
    {
        var resolved = WindowGeometry.TryResolve(new AppSettings(), SingleScreen, out var bounds);

        Assert.False(resolved);
        Assert.Equal(Rectangle.Empty, bounds);
    }

    [Fact]
    public void SavedPositionOnScreen_ResolvesExactBounds()
    {
        var settings = new AppSettings { WindowX = 120, WindowY = 80, WindowWidth = 1280, WindowHeight = 800 };

        Assert.True(WindowGeometry.TryResolve(settings, SingleScreen, out var bounds));
        Assert.Equal(new Rectangle(120, 80, 1280, 800), bounds);
    }

    [Fact]
    public void EntirelyOffAllScreens_DoesNotResolve_ForCenterScreenFallback()
    {
        // 外接显示器已移除：保存位置完全在屏外。
        var settings = new AppSettings { WindowX = 4000, WindowY = 200, WindowWidth = 1100, WindowHeight = 700 };

        Assert.False(WindowGeometry.TryResolve(settings, SingleScreen, out _));
    }

    [Fact]
    public void NegativeCoordinates_OnLeftMonitor_Resolves()
    {
        var screens = new[] { new Rectangle(-1920, 0, 1920, 1040), SingleScreen[0] };
        var settings = new AppSettings { WindowX = -1600, WindowY = 60, WindowWidth = 1100, WindowHeight = 700 };

        Assert.True(WindowGeometry.TryResolve(settings, screens, out var bounds));
        Assert.Equal(new Rectangle(-1600, 60, 1100, 700), bounds);
    }

    [Fact]
    public void PartiallyVisibleWindow_Resolves()
    {
        // 标题栏可见即可：窗口大部分在屏外但相交。
        var settings = new AppSettings { WindowX = 1900, WindowY = 100, WindowWidth = 1100, WindowHeight = 700 };

        Assert.True(WindowGeometry.TryResolve(settings, SingleScreen, out _));
    }

    [Fact]
    public void RestoreSize_FallsBackToDefaultsForNonPositive()
    {
        var size = WindowGeometry.RestoreSize(new AppSettings { WindowWidth = 0, WindowHeight = -5 });

        Assert.Equal(new Size(AppSettings.DefaultWindowWidth, AppSettings.DefaultWindowHeight), size);
    }
}
