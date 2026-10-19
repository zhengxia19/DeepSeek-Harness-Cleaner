using System.Drawing;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// N-03 窗口几何恢复（design.md §9）：把已保存的窗口位置/大小解析为可恢复矩形。
/// 纯函数：调用方（UI）传入各屏幕工作区；无任何工作区与保存矩形相交（全出屏，
/// 如外接显示器已移除）时返回 false，由调用方回退 CenterScreen。
/// </summary>
public static class WindowGeometry
{
    /// <summary>
    /// 有保存位置（WindowX/WindowY 非 null）且矩形与任一工作区相交 → true 并给出钳制后的恢复矩形；
    /// 未保存位置或全出屏 → false（尺寸仍可通过 <see cref="RestoreSize"/> 单独恢复）。
    /// </summary>
    public static bool TryResolve(AppSettings settings, IReadOnlyList<Rectangle> workingAreas, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (settings.WindowX is not int x || settings.WindowY is not int y)
            return false;

        var size = RestoreSize(settings);
        var candidate = new Rectangle(x, y, size.Width, size.Height);
        foreach (var area in workingAreas)
        {
            if (area.IntersectsWith(candidate))
            {
                bounds = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>已保存的窗口外层尺寸（store 已钳制 320..8192；此处仅防非正退化值）。</summary>
    public static Size RestoreSize(AppSettings settings)
    {
        var width = settings.WindowWidth > 0 ? settings.WindowWidth : AppSettings.DefaultWindowWidth;
        var height = settings.WindowHeight > 0 ? settings.WindowHeight : AppSettings.DefaultWindowHeight;
        return new Size(width, height);
    }
}
