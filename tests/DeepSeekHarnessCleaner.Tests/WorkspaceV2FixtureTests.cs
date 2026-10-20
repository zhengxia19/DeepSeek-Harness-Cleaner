using System.Text.Json;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

/// <summary>Workspace v2 合成介质夹具；不声称是脱敏真实发行样本。</summary>
public sealed class WorkspaceV2FixtureTests
{
    [Fact]
    public void ValidV2_ContainsArchivePinsAndOrderedMultipleWorkspaces()
    {
        using var root = FixtureRoot.Create();
        var path = WorkspaceV2Fixture.Write(root);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var global = json.RootElement.GetProperty("global");
        Assert.Equal(2, json.RootElement.GetProperty("unit").GetProperty("version").GetInt32());
        Assert.Equal("synthetic-a", global.GetProperty("archivedSessionIds")[0].GetString());
        Assert.Equal("synthetic-b", global.GetProperty("pinnedSessionIds")[0].GetString());
        var workspaces = json.RootElement.GetProperty("tables").GetProperty("workspaces");
        Assert.Equal("synthetic-a", workspaces.GetProperty("workspace-1").GetProperty("sessionIds")[0].GetString());
        Assert.Equal("synthetic-b", workspaces.GetProperty("workspace-2").GetProperty("sessionIds")[0].GetString());
        Assert.True(File.Exists(root.Factory.WriteValidV0Session("synthetic-project", "synthetic-a")));
        Assert.False(File.Exists(Path.Combine(root.SessionsRoot, "synthetic-project", "synthetic-missing", "session.jsonl")));
    }

    [Fact]
    public void InvalidStates_AreDistinctAndCannotBeMistakenForCleanWorkspace()
    {
        using var root = FixtureRoot.Create();
        foreach (var state in new[] { WorkspaceV2Variant.DuplicateId, WorkspaceV2Variant.DuplicateJsonProperty,
                     WorkspaceV2Variant.CorruptJson, WorkspaceV2Variant.UnknownVersion,
                     WorkspaceV2Variant.PendingMutation, WorkspaceV2Variant.Uninitialized })
        {
            var path = WorkspaceV2Fixture.Write(root, state);
            var content = File.ReadAllText(path);
            if (state == WorkspaceV2Variant.CorruptJson)
            {
                Assert.Throws<JsonException>(() => JsonDocument.Parse(content));
                continue;
            }
            using var json = JsonDocument.Parse(content);
            if (state == WorkspaceV2Variant.DuplicateId)
                Assert.Equal("synthetic-a", json.RootElement.GetProperty("global").GetProperty("archivedSessionIds")[1].GetString());
            if (state == WorkspaceV2Variant.DuplicateJsonProperty)
                Assert.Equal(2, json.RootElement.EnumerateObject().Count(x => x.Name == "global"));
            if (state == WorkspaceV2Variant.UnknownVersion)
                Assert.Equal(99, json.RootElement.GetProperty("unit").GetProperty("version").GetInt32());
            if (state == WorkspaceV2Variant.PendingMutation)
                Assert.Equal("delete", json.RootElement.GetProperty("global").GetProperty("pendingMutation").GetProperty("operation").GetString());
            if (state == WorkspaceV2Variant.Uninitialized)
                Assert.False(json.RootElement.GetProperty("global").GetProperty("initialized").GetBoolean());
        }
    }

    [Fact]
    public void ConcurrentMutationFixture_ChangesFileIdentityByContent()
    {
        using var root = FixtureRoot.Create();
        var path = WorkspaceV2Fixture.Write(root);
        var original = File.ReadAllBytes(path);
        WorkspaceV2Fixture.MutateFile(path);
        Assert.False(original.SequenceEqual(File.ReadAllBytes(path)));
    }

    [Fact]
    public void SymlinkFixture_StaysInsideTemporarySandboxOrReportsUnsupported()
    {
        using var root = FixtureRoot.Create();
        var target = WorkspaceV2Fixture.Write(root);
        var link = Path.Combine(root.StoragesRoot, "workspace-link.json");
        try
        {
            File.CreateSymbolicLink(link, target);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(File.ReadAllText(target), File.ReadAllText(link));
            File.Delete(link); // 不让 FixtureRoot.Dispose 跟随链接
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows 无符号链接权限时不得假定测试过了链接防护。
            throw new Xunit.Sdk.SkipException("Symbolic link creation unavailable on this runner: " + ex.GetType().Name);
        }
    }
}

public enum WorkspaceV2Variant
{
    Valid,
    DuplicateId,
    DuplicateJsonProperty,
    CorruptJson,
    UnknownVersion,
    PendingMutation,
    Uninitialized,
}

/// <summary>严格使用临时夹具目录和不具备真实标识含义的常量。</summary>
public static class WorkspaceV2Fixture
{
    public static string Write(FixtureRoot root, WorkspaceV2Variant variant = WorkspaceV2Variant.Valid)
    {
        var header = new { name = "workspace", version = variant == WorkspaceV2Variant.UnknownVersion ? 99 : 2 };
        var global = new
        {
            initialized = variant != WorkspaceV2Variant.Uninitialized,
            workspaceIds = new[] { "workspace-1", "workspace-2" },
            archivedSessionIds = variant == WorkspaceV2Variant.DuplicateId
                ? new[] { "synthetic-a", "synthetic-a" }
                : new[] { "synthetic-a" },
            pinnedSessionIds = new[] { "synthetic-b" },
        };
        var records = new Dictionary<string, object>
        {
            ["workspace-1"] = new { path = Path.Combine(root.Root, "synthetic-work-1"),
                title = "Synthetic One", sessionIds = new[] { "synthetic-a" },
                createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
            ["workspace-2"] = new { path = Path.Combine(root.Root, "synthetic-work-2"),
                title = "Synthetic Two", sessionIds = new[] { "synthetic-b", "synthetic-missing" },
                createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" },
        };
        var fields = new Dictionary<string, object>
        {
            ["unit"] = header,
            ["global"] = global,
            ["tables"] = new { workspaces = records },
        };
        var json = JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true });
        if (variant == WorkspaceV2Variant.PendingMutation)
            json = json.Replace("\"workspaceIds\":", "\"pendingMutation\": {\"operation\":\"delete\",\"workspaceId\":\"workspace-1\"}, \"workspaceIds\":", StringComparison.Ordinal);
        if (variant == WorkspaceV2Variant.DuplicateJsonProperty)
            json = json.Insert(json.LastIndexOf('}'), ", \"global\": {}");
        if (variant == WorkspaceV2Variant.CorruptJson)
            json = json[..^1];
        var path = Path.Combine(root.StoragesRoot, "workspace.json");
        File.WriteAllText(path, json);
        return path;
    }

    public static void MutateFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        stream.Write("\n"u8);
        stream.Flush(flushToDisk: true);
    }
}
