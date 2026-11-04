using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using DeepSeekHarnessCleaner.UI;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceSyncOptionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("production")]
    [InlineData("active")]
    [InlineData("unknown-process")]
    [InlineData("source-changed")]
    [InlineData("unknown-field")]
    [InlineData("plan-mismatch")]
    [InlineData("root-fingerprint")]
    [InlineData("backup-inside")]
    [InlineData("backup-link")]
    [InlineData("marker")]
    [InlineData("candidate")]
    [InlineData("no-references")]
    public void AvailabilityIsReadOnlyAndMatchesCurrentPreconditions(string fault)
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var fixture = new WorkspaceFixture(root);
        var document = fixture.CreateDocument();
        if (fault == "unknown-field") document["global"]!["future"] = true;
        if (fault == "no-references")
        {
            document["global"]!["archivedSessionIds"] = new System.Text.Json.Nodes.JsonArray();
            document["tables"]!["workspaces"]!["synthetic-a"]!["sessionIds"] = new System.Text.Json.Nodes.JsonArray(WorkspaceFixture.PinnedId);
        }
        root.Factory.WriteFile(root.StoragesRoot, "workspace.json", FixtureFactory.Utf8(document.ToJsonString()));
        fixture.WriteSession(WorkspaceFixture.ArchivedId);
        var sessions = new InventoryService(new UiScanSource()).Scan(root.SessionsRoot).Sessions;
        var cleanup = new SessionCleanupPreparation().Build(root.SessionsRoot, sessions).Plan!;
        var before = new PreCleanupAuditor().Audit(root.SessionsRoot, [WorkspaceFixture.ArchivedId], cleanup);
        var backup = Directory.CreateDirectory(Path.Combine(root.Root, "metadata-backups")).FullName;
        if (fault == "source-changed") File.AppendAllText(fixture.WorkspacePath, " ");
        if (fault == "plan-mismatch") cleanup = cleanup with { PlanId = Guid.NewGuid() };
        if (fault == "root-fingerprint") { cleanup = cleanup with { RootFingerprint = "wrong" }; before = before with { RootFingerprint = "wrong" }; }
        if (fault == "backup-inside") backup = root.StoragesRoot;
        if (fault == "backup-link") { var link = Path.Combine(root.Root, "linked"); Directory.CreateSymbolicLink(link, outside.Root); backup = link; }
        if (fault == "marker") File.WriteAllText(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-sync.lock"), "old-operation");
        if (fault == "candidate") File.WriteAllText(Path.Combine(root.StoragesRoot, ".dshcleaner-workspace-unknown.tmp"), "preserve");
        var probe = fault switch
        {
            "active" => new HarnessProcessProbe(() => [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]),
            "unknown-process" => new HarnessProcessProbe(() => throw new InvalidOperationException()),
            _ => new HarnessProcessProbe(() => []),
        };
        var files = Directory.GetFiles(root.DshHome, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        var coordinator = fault == "production" ? new WorkspaceSyncCoordinator(probe) : new WorkspaceSyncCoordinator(root.DshHome, probe);
        var option = coordinator.GetOption(cleanup, before, backup);
        Assert.Equal(fault == "valid", option.Available);
        Assert.Equal(cleanup.PlanId, option.CleanupPlanId);
        if (fault == "valid") Assert.Equal(2, option.ReferenceCount);
        else Assert.False(string.IsNullOrWhiteSpace(option.Reason));
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root.Root, "metadata-backups")));
    }
}
