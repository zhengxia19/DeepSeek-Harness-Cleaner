using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceReferenceTests
{
    [Fact]
    public void Known_IndexesAllThreeKindsAndExactSnapshotLocations_ReadOnly()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var original = File.ReadAllBytes(fixture.WorkspacePath);
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Known, result.Status);
        Assert.Equal(5, result.References.Count);
        Assert.Equal(Path.GetFullPath(fixture.WorkspacePath), result.EvidencePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), result.SnapshotSha256);
        Assert.Contains(result.References, x => x.SessionId == WorkspaceFixture.ArchivedId && x.Kind == WorkspaceReferenceKind.Archived && x.JsonPointer == "/global/archivedSessionIds/0" && x.WorkspaceId is null);
        Assert.Contains(result.References, x => x.SessionId == WorkspaceFixture.PinnedId && x.Kind == WorkspaceReferenceKind.Pinned && x.JsonPointer == "/global/pinnedSessionIds/0");
        Assert.Contains(result.References, x => x.SessionId == WorkspaceFixture.MissingId && x.Kind == WorkspaceReferenceKind.WorkspaceMember && x.WorkspaceId == "synthetic-b" && x.JsonPointer == "/tables/workspaces/synthetic-b/sessionIds/0");
        Assert.Null(result.Reason);
        Assert.Null(result.ErrorPointer);
        Assert.Empty(Directory.GetFiles(root.SessionsRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(original, File.ReadAllBytes(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("corrupt", WorkspaceReferenceStatus.Unknown)]
    [InlineData("unknown-version", WorkspaceReferenceStatus.Unsupported)]
    [InlineData("uninitialized", WorkspaceReferenceStatus.Unknown)]
    [InlineData("pending-create", WorkspaceReferenceStatus.Unknown)]
    [InlineData("pending-delete", WorkspaceReferenceStatus.Unknown)]
    [InlineData("duplicate-archive", WorkspaceReferenceStatus.Unknown)]
    [InlineData("duplicate-member", WorkspaceReferenceStatus.Unknown)]
    public void UnsafeFixture_IsNotKnownNoReferences(string scenario, WorkspaceReferenceStatus status)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write(scenario);
        var before = File.ReadAllBytes(fixture.WorkspacePath);
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(status, result.Status);
        Assert.Empty(result.References);
        Assert.NotNull(result.Reason);
        Assert.Equal(before, File.ReadAllBytes(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData("null-global")]
    [InlineData("null-transaction")]
    [InlineData("unknown-transaction")]
    [InlineData("missing-table")]
    [InlineData("unlisted-record")]
    [InlineData("listed-missing-record")]
    [InlineData("duplicate-workspace")]
    [InlineData("duplicate-pin")]
    [InlineData("archive-pin-overlap")]
    [InlineData("blank-member")]
    [InlineData("member-type")]
    [InlineData("cross-workspace-member")]
    [InlineData("bad-date")]
    public void InvalidState_ProducesUnknownWithReasonAndEvidence(string scenario)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var doc = fixture.CreateDocument();
        switch (scenario)
        {
            case "null-global": doc["global"] = null; break;
            case "null-transaction": doc["global"]!["pendingMutation"] = null; break;
            case "unknown-transaction": doc["global"]!["pendingMutation"] = new JsonObject { ["operation"] = "future" }; break;
            case "missing-table": doc.Remove("tables"); break;
            case "unlisted-record": doc["global"]!["workspaceIds"] = new JsonArray("synthetic-a"); break;
            case "listed-missing-record": doc["tables"]!["workspaces"]!.AsObject().Remove("synthetic-b"); break;
            case "duplicate-workspace": doc["global"]!["workspaceIds"]!.AsArray().Add("synthetic-a"); break;
            case "duplicate-pin": doc["global"]!["pinnedSessionIds"]!.AsArray().Add(WorkspaceFixture.PinnedId); break;
            case "archive-pin-overlap": doc["global"]!["pinnedSessionIds"]!.AsArray().Add(WorkspaceFixture.ArchivedId); break;
            case "blank-member": doc["tables"]!["workspaces"]!["synthetic-b"]!["sessionIds"] = new JsonArray(" "); break;
            case "member-type": doc["tables"]!["workspaces"]!["synthetic-b"]!["sessionIds"] = new JsonArray(12); break;
            case "cross-workspace-member": doc["tables"]!["workspaces"]!["synthetic-b"]!["sessionIds"] = new JsonArray(WorkspaceFixture.PinnedId); break;
            case "bad-date": doc["tables"]!["workspaces"]!["synthetic-b"]!["updatedAt"] = "not a date"; break;
        }
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(doc.ToJsonString()));
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Unknown, result.Status);
        Assert.Empty(result.References);
        Assert.NotNull(result.ErrorPointer);
        Assert.NotNull(result.Reason);
        Assert.NotNull(result.SnapshotSha256);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("unit")]
    [InlineData("record")]
    [InlineData("unknown-nested")]
    public void DuplicateFieldsAnywhere_AreUnknown(string level)
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var text = fixture.CreateDocument().ToJsonString();
        text = level switch
        {
            "global" => text.Replace("\"initialized\":true", "\"initialized\":false,\"initialized\":true", StringComparison.Ordinal),
            "unit" => text.Replace("\"version\":2", "\"version\":1,\"version\":2", StringComparison.Ordinal),
            "record" => text.Replace("\"title\":\"synthetic-b\"", "\"title\":\"before\",\"title\":\"synthetic-b\"", StringComparison.Ordinal),
            _ => text.Insert(1, "\"extra\":[{\"x\":1,\"x\":2}],"),
        };
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(text));
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Unknown, result.Status);
        Assert.Equal("WORKSPACE_DUPLICATE_FIELD", result.Reason);
        Assert.NotNull(result.ErrorPointer);
    }

    [Fact]
    public void KnownEmptyAndDefaultArrays_AreDistinctFromLegacyArchiveUnknown()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var doc = fixture.CreateDocument();
        doc["global"]!.AsObject().Remove("archivedSessionIds");
        doc["global"]!.AsObject().Remove("pinnedSessionIds");
        doc["global"]!["workspaceIds"] = new JsonArray();
        doc["global"]!["defaultWorkspaceId"] = "deleted-workspace-is-allowed";
        doc["tables"]!["workspaces"] = new JsonObject();
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(doc.ToJsonString()));
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Known, result.Status);
        Assert.Empty(result.References);
        Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
    }

    [Fact]
    public void WorkspaceKeys_UseEscapedJsonPointers()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var doc = fixture.CreateDocument();
        var workspaces = doc["tables"]!["workspaces"]!.AsObject();
        var record = workspaces["synthetic-b"]!.DeepClone();
        workspaces.Remove("synthetic-b");
        workspaces["a/b~c"] = record;
        doc["global"]!["workspaceIds"] = new JsonArray("synthetic-a", "a/b~c");
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(doc.ToJsonString()));
        var result = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Known, result.Status);
        Assert.Contains(result.References, x => x.WorkspaceId == "a/b~c" && x.JsonPointer == "/tables/workspaces/a~1b~0c/sessionIds/0");
    }

    [Fact]
    public void MissingPerRecordAndAmbiguousLayouts_DoNotImplyEmpty()
    {
        using var root = FixtureRoot.Create();
        var reader = new WorkspaceReferenceReader();
        Assert.Equal(WorkspaceReferenceStatus.Unknown, reader.Read(root.DshHome).Status);
        var layout = Path.Combine(root.StoragesRoot, "workspace");
        Directory.CreateDirectory(layout);
        Assert.Equal(WorkspaceReferenceStatus.Unsupported, reader.Read(root.DshHome).Status);
        Assert.Equal(layout, reader.Read(root.DshHome).EvidencePath);
        new WorkspaceFixture(root).Write("valid");
        Assert.Equal("WORKSPACE_LAYOUT_AMBIGUOUS", reader.Read(root.DshHome).Reason);
        Assert.Equal(WorkspaceReferenceStatus.Unknown, reader.Read(root.DshHome).Status);
    }

    [Fact]
    public void LockedOversizeAndChangedFile_AreHandledConservatively()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        fixture.Write("valid");
        var reader = new WorkspaceReferenceReader();
        var initial = reader.Read(root.DshHome);
        using (var locked = new FileStream(fixture.WorkspacePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(WorkspaceReferenceStatus.Unknown, reader.Read(root.DshHome).Status);
        fixture.ChangeWorkspace();
        var changed = reader.Read(root.DshHome);
        Assert.Equal(WorkspaceReferenceStatus.Known, changed.Status);
        Assert.NotEqual(initial.SnapshotSha256, changed.SnapshotSha256);
        using (var oversize = new FileStream(fixture.WorkspacePath, FileMode.Create)) oversize.SetLength(8 * 1024 * 1024 + 1);
        Assert.Equal("WORKSPACE_TOO_LARGE", reader.Read(root.DshHome).Reason);
        Assert.Null(new WorkspaceArchiveReader().Read(root.DshHome));
    }

    [Fact]
    public void SharedLoading_PreservesUtf8BomAndArchiveCompatibility()
    {
        using var root = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(fixture.CreateDocument().ToJsonString())).ToArray();
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", bytes);
        Assert.Equal(WorkspaceFixture.ArchivedId, Assert.Single(new WorkspaceArchiveReader().Read(root.DshHome)!));
        Assert.Equal(WorkspaceReferenceStatus.Known, new WorkspaceReferenceReader().Read(root.DshHome).Status);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.WorkspacePath));
    }

    [Fact]
    public void SymbolicLink_IsUnknown_OutsideSnapshotUnchanged()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var target = new WorkspaceFixture(outside).Write("valid");
        var before = File.ReadAllBytes(target);
        var link = Path.Combine(root.StoragesRoot, "workspace.json");
        try
        {
            File.CreateSymbolicLink(link, target);
            var result = new WorkspaceReferenceReader().Read(root.DshHome);
            Assert.Equal(WorkspaceReferenceStatus.Unknown, result.Status);
            Assert.Equal(PathGuard.PathReparseErrorCode, result.Reason);
            Assert.Empty(result.References);
            Assert.Equal(before, File.ReadAllBytes(target));
        }
        finally { if (File.Exists(link)) File.Delete(link); }
    }
}
