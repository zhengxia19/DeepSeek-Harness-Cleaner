using System.Drawing;
using System.Text.Json;

namespace DeepSeekHarnessCleaner.Infrastructure;

public sealed record WindowRectangle(int X, int Y, int Width, int Height);
public sealed record WindowStateRecord(int SchemaVersion, WindowRectangle NormalBounds, string WindowState, int SavedDpi);
public sealed record WindowStateLoadResult(WindowStateRecord? State, bool CanSave, string? Error);

public sealed class WindowStateStore
{
    public const string FileName = "DeepSeekHarnessCleaner.window-state.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public string FilePath { get; }
    public WindowStateStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(AppContext.BaseDirectory, FileName));

    public WindowStateLoadResult Load()
    {
        try
        {
            RejectReparse(FilePath);
            if (!File.Exists(FilePath)) return new(null, true, null);
            if (new FileInfo(FilePath).Length > 16384) throw new IOException("窗口记录过大。");
            var state = JsonSerializer.Deserialize<WindowStateRecord>(File.ReadAllText(FilePath), Options);
            if (state is null || !Valid(state)) return new(null, false, "窗口记录损坏或版本不支持，已使用默认位置；原文件保留。");
            return new(state, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            return new(null, false, "无法读取窗口记录：" + ex.Message);
        }
    }

    public string? Save(WindowStateRecord state)
    {
        string? temp = null;
        try
        {
            if (!Valid(state)) return "窗口记录数值无效。";
            var existing = Load();
            if (!existing.CanSave) return existing.Error;
            var directory = Path.GetDirectoryName(FilePath)!;
            RejectReparse(directory);
            Directory.CreateDirectory(directory);
            temp = Path.Combine(directory, Path.GetFileName(FilePath) + ".tmp-" + Guid.NewGuid().ToString("N"));
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, state, Options);
                output.Flush(true);
            }
            RejectReparse(FilePath);
            File.Move(temp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "无法保存窗口位置：" + ex.Message;
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Rectangle Fit(WindowStateRecord state, IReadOnlyList<Rectangle> workAreas, Size minimum, int currentDpi)
    {
        var b = state.NormalBounds;
        var original = new Rectangle(b.X, b.Y, b.Width, b.Height);
        var work = workAreas.OrderByDescending(area => Rectangle.Intersect(area, original).Width * (long)Rectangle.Intersect(area, original).Height).First();
        var ratio = (double)currentDpi / state.SavedDpi;
        var width = Math.Min(work.Width, Math.Max(minimum.Width, (int)Math.Round(b.Width * ratio)));
        var height = Math.Min(work.Height, Math.Max(minimum.Height, (int)Math.Round(b.Height * ratio)));
        return new Rectangle(Math.Clamp(b.X, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(b.Y, work.Top, Math.Max(work.Top, work.Bottom - height)), width, height);
    }

    private static bool Valid(WindowStateRecord state) => state.SchemaVersion == 1 && state.NormalBounds is { } b
        && b.Width is > 0 and <= 16384 && b.Height is > 0 and <= 16384
        && b.X is >= -1000000 and <= 1000000 && b.Y is >= -1000000 and <= 1000000
        && state.WindowState is "Normal" or "Maximized" && state.SavedDpi is >= 48 and <= 768;

    private static void RejectReparse(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
            current = current is DirectoryInfo directory ? directory.Parent : new FileInfo(current.FullName).Directory)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("窗口记录路径包含重解析点。");
    }
}
