using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceFixtureTests
{
    [Fact]
    public void ValidFixture_HasArchivePinTwoWorkspacesAndMissingLog()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture.Write("valid")));
        var global = doc.RootElement.GetProperty("global");
        Assert.Equal(WorkspaceFixture.ArchivedId, global.GetProperty("archivedSessionIds")[0].GetString());
        Assert.Equal(WorkspaceFixture.PinnedId, global.GetProperty("pinnedSessionIds")[0].GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("tables").GetProperty("workspaces").EnumerateObject().Count());
        var archived = fixture.WriteSession(WorkspaceFixture.ArchivedId);
        fixture.WriteSession(WorkspaceFixture.PinnedId);
        Assert.Equal(2, Directory.GetFiles(root.SessionsRoot, "*.jsonl", SearchOption.AllDirectories).Length);
        using var header = JsonDocument.Parse(File.ReadAllText(archived));
        Assert.Equal("session", header.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(WorkspaceFixture.ArchivedId, header.RootElement.GetProperty("id").GetString());
        Assert.Contains(WorkspaceFixture.MissingId, File.ReadAllText(fixture.WorkspacePath));
        Assert.False(Directory.Exists(Path.Combine(root.SessionsRoot, "synthetic-b", WorkspaceFixture.MissingId)));
    }

    [Theory]
    [InlineData("duplicate-archive")]
    [InlineData("duplicate-member")]
    [InlineData("unknown-version")]
    [InlineData("uninitialized")]
    [InlineData("pending-create")]
    [InlineData("pending-delete")]
    public void NegativeFixture_ContainsRequestedAnomaly(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture.Write(scenario)));
        var global = doc.RootElement.GetProperty("global");
        switch (scenario)
        {
            case "duplicate-archive":
            case "duplicate-member":
                var array = scenario == "duplicate-archive" ? global.GetProperty("archivedSessionIds")
                    : doc.RootElement.GetProperty("tables").GetProperty("workspaces").GetProperty("synthetic-a").GetProperty("sessionIds");
                Assert.True(array.GetArrayLength() > array.EnumerateArray().Select(x => x.GetString()).Distinct().Count());
                break;
            case "unknown-version": Assert.Equal(999, doc.RootElement.GetProperty("unit").GetProperty("version").GetInt32()); break;
            case "uninitialized": Assert.False(global.GetProperty("initialized").GetBoolean()); break;
            default:
                Assert.Equal(scenario == "pending-create" ? "create" : "delete", global.GetProperty("pendingMutation").GetProperty("operation").GetString());
                break;
        }
    }

    [Fact]
    public void CorruptAndInvalidScenario_AreExplicit()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(File.ReadAllText(fixture.Write("corrupt"))));
        var before = File.ReadAllBytes(fixture.WorkspacePath);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Write("invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.WriteSession("../outside"));
        Assert.Equal(before, File.ReadAllBytes(fixture.WorkspacePath));
    }

    [Fact]
    public void ChangeFixture_ChangesFingerprintWithoutTouchingUnselectedOrOutsideSentinel()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var unselected = fixture.WriteSession(WorkspaceFixture.PinnedId);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("synthetic sentinel"));
        var before = SHA256.HashData(File.ReadAllBytes(fixture.WorkspacePath));
        var unselectedHash = SHA256.HashData(File.ReadAllBytes(unselected));
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        fixture.ChangeWorkspace();
        Assert.False(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.WorkspacePath))));
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture.WorkspacePath));
        Assert.Equal(2, doc.RootElement.GetProperty("unit").GetProperty("version").GetInt32());
        Assert.Equal(unselectedHash, SHA256.HashData(File.ReadAllBytes(unselected)));
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Fact]
    public void SymbolicLinkFixture_PointsOnlyToSeparateSyntheticRoot()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var targetFixture = new WorkspaceFixture(outside);
        targetFixture.Write("valid");
        var targetHash = SHA256.HashData(File.ReadAllBytes(targetFixture.WorkspacePath));
        var link = fixture.WorkspacePath;
        try
        {
            File.CreateSymbolicLink(link, targetFixture.WorkspacePath);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(Path.GetFullPath(targetFixture.WorkspacePath), new FileInfo(link).ResolveLinkTarget(true)!.FullName);
        }
        finally
        {
            // Delete only the link, before either owned synthetic root is disposed.
            if (File.Exists(link)) File.Delete(link);
        }
        Assert.Equal(targetHash, SHA256.HashData(File.ReadAllBytes(targetFixture.WorkspacePath)));
    }
}
