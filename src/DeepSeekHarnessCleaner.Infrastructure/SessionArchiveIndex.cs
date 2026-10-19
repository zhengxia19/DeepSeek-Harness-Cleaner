using System.Text.Json;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>
/// 归档标志只读索引结果（N-01/D5）。Status 为 null 表示归档信息可用（集合可为空 = 无归档会话）；
/// Status 非空表示不可用，此时 ArchivedSessionIds 恒为空集，调用方按“全部未归档”显示并给出状态提示。
/// </summary>
public sealed record ArchiveIndexResult(IReadOnlySet<string> ArchivedSessionIds, string? Status)
{
    public bool IsAvailable => Status is null;
}

/// <summary>
/// N-01：从会话根父目录推导 <c>storages\workspace.json</c>，只读解析
/// <c>global.archivedSessionIds</c>（JSON 字符串数组）。该文件永远只读：本类不创建、写入或删除任何文件。
/// 缺失/损坏/超限（≤32MiB 门禁）/结构未知/reparse 一律安全回退为空集 + 状态码（D5），扫描不失败。
/// </summary>
public sealed class SessionArchiveIndex
{
    public const string StoragesDirName = "storages";
    public const string WorkspaceFileName = "workspace.json";

    /// <summary>workspace.json 读取上限（复用行/解压门禁思路，D5“异常大”按不可用处理）。</summary>
    public const long MaxWorkspaceBytes = 32L * 1024 * 1024;

    public const string StatusMissing = "ARCHIVE_FILE_MISSING";
    public const string StatusTooLarge = "ARCHIVE_FILE_TOO_LARGE";
    public const string StatusUnreadable = "ARCHIVE_FILE_UNREADABLE";
    public const string StatusReparse = PathGuard.PathReparseErrorCode;
    public const string StatusJsonInvalid = "ARCHIVE_JSON_INVALID";
    public const string StatusStructureUnknown = "ARCHIVE_STRUCTURE_UNKNOWN";

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    private static readonly ArchiveIndexResult UnavailableMissing = new(Empty, StatusMissing);
    private static readonly ArchiveIndexResult UnavailableTooLarge = new(Empty, StatusTooLarge);
    private static readonly ArchiveIndexResult UnavailableUnreadable = new(Empty, StatusUnreadable);
    private static readonly ArchiveIndexResult UnavailableReparse = new(Empty, StatusReparse);
    private static readonly ArchiveIndexResult UnavailableJsonInvalid = new(Empty, StatusJsonInvalid);
    private static readonly ArchiveIndexResult UnavailableStructure = new(Empty, StatusStructureUnknown);

    /// <summary>
    /// 只读读取会话根对应的归档标志。sessionRoot 为 sessions 根；workspace.json 位于其父目录的
    /// storages 子目录（architecture.md §5 三个受控区域同级）。
    /// </summary>
    public ArchiveIndexResult Read(string sessionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);

        string fullPath;
        try
        {
            var trimmed = Path.GetFullPath(sessionRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent))
                return UnavailableMissing;
            fullPath = Path.Combine(parent, StoragesDirName, WorkspaceFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return UnavailableStructure;
        }

        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return UnavailableMissing;
        }
        catch (DirectoryNotFoundException)
        {
            return UnavailableMissing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return ex is ArgumentException ? UnavailableStructure : UnavailableUnreadable;
        }

        if ((attrs & FileAttributes.ReparsePoint) != 0)
            return UnavailableReparse;
        if ((attrs & FileAttributes.Directory) != 0)
            return UnavailableStructure;

        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex is UnauthorizedAccessException ? UnavailableUnreadable : UnavailableMissing;
        }

        if (length > MaxWorkspaceBytes)
            return UnavailableTooLarge;

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return UnavailableUnreadable;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return UnavailableJsonInvalid;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return UnavailableStructure;

            // 真实结构：{ "unit": {…}, "global": { "archivedSessionIds": [ … ] }, "tables": {…} }。
            // global 缺失视为“无归档标志”（可用、空集）；global 存在但非对象 → 结构未知。
            var ids = Empty;
            if (doc.RootElement.TryGetProperty("global", out var global))
            {
                if (global.ValueKind != JsonValueKind.Object)
                    return UnavailableStructure;

                if (global.TryGetProperty("archivedSessionIds", out var array))
                {
                    if (array.ValueKind != JsonValueKind.Array)
                        return UnavailableStructure;

                    var collected = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var element in array.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.String)
                            return UnavailableStructure;
                        var id = element.GetString();
                        if (!string.IsNullOrEmpty(id))
                            collected.Add(id);
                    }

                    ids = collected;
                }
            }

            return new ArchiveIndexResult(ids, null);
        }
    }
}
