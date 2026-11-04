using System.Text.Json.Nodes;

namespace DeepSeekHarnessCleaner.Tests.Fixtures;

/// <summary>Synthetic v2 single-file data from the T01 source contract; no release sample is verified.</summary>
internal sealed class WorkspaceFixture
{
    internal const string ArchivedId = "synthetic-archived";
    internal const string PinnedId = "synthetic-pinned";
    internal const string MissingId = "synthetic-missing";
    private readonly FixtureRoot _root;

    internal WorkspaceFixture(FixtureRoot root) => _root = root;

    internal string WorkspacePath => Path.Combine(_root.StoragesRoot, "workspace.json");

    internal JsonObject CreateDocument()
    {
        JsonObject Workspace(string name, params string[] ids) => new()
        {
            ["path"] = Path.Combine(_root.Root, name),
            ["title"] = name,
            ["sessionIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["createdAt"] = "2026-01-01T00:00:00Z",
            ["updatedAt"] = "2026-01-01T00:00:00Z",
        };
        return new JsonObject
        {
            ["unit"] = new JsonObject { ["name"] = "workspace", ["version"] = 2 },
            ["global"] = new JsonObject
            {
                ["initialized"] = true,
                ["workspaceIds"] = new JsonArray("synthetic-a", "synthetic-b"),
                ["defaultWorkspaceId"] = "synthetic-a",
                ["archivedSessionIds"] = new JsonArray(ArchivedId),
                ["pinnedSessionIds"] = new JsonArray(PinnedId),
            },
            ["tables"] = new JsonObject
            {
                ["workspaces"] = new JsonObject
                {
                    ["synthetic-a"] = Workspace("synthetic-a", ArchivedId, PinnedId),
                    ["synthetic-b"] = Workspace("synthetic-b", MissingId),
                },
            },
        };
    }

    internal string Write(string scenario)
    {
        var document = CreateDocument();
        switch (scenario)
        {
            case "valid": break;
            case "duplicate-archive": document["global"]!["archivedSessionIds"]!.AsArray().Add(ArchivedId); break;
            case "duplicate-member": document["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"]!.AsArray().Add(PinnedId); break;
            case "unknown-version": document["unit"]!["version"] = 999; break;
            case "uninitialized": document["global"]!["initialized"] = false; break;
            case "pending-create":
            case "pending-delete":
                document["global"]!["pendingMutation"] = new JsonObject
                {
                    ["operation"] = scenario == "pending-create" ? "create" : "delete",
                    ["workspaceId"] = "synthetic-b",
                };
                break;
            case "corrupt":
                return _root.Factory.WriteFile(_root.StoragesRoot, "workspace.json", FixtureFactory.Utf8("{\"unit\":"));
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return _root.Factory.WriteFile(_root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(document.ToJsonString()));
    }

    internal string WriteSession(string id)
    {
        if (id is not (ArchivedId or PinnedId)) throw new ArgumentOutOfRangeException(nameof(id));
        // Header-only logs avoid storing chat text; paths refer only to this temporary fixture.
        var header = new JsonObject
        {
            ["type"] = "session",
            ["id"] = id,
            ["version"] = 4,
            ["createdAt"] = 1767225600000L,
            ["cwd"] = Path.Combine(_root.Root, "synthetic-a"),
        };
        return _root.Factory.WriteSessionFile("synthetic-a", id, "session.v4.jsonl",
            FixtureFactory.Utf8(header.ToJsonString() + "\n"));
    }

    internal void ChangeWorkspace() => File.AppendAllText(WorkspacePath, "\n ");
}
