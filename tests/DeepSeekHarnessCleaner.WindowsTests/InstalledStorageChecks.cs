using System.Text.Json;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static int RunInstalledStorageChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures", "fx-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, ".dsh");
        var sessions = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
        var storages = Directory.CreateDirectory(Path.Combine(home, "storages")).FullName;
        Directory.CreateDirectory(Path.Combine(home, "attachments"));
        var profile = Directory.CreateDirectory(Path.Combine(home, "profiles", "desktop")).FullName;
        var backups = Directory.CreateDirectory(Path.Combine(root, "metadata-backups")).FullName;
        File.WriteAllText(Path.Combine(profile, "package.json"), """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""");
        File.WriteAllText(Path.Combine(profile, "cordis.patch.yml"), "[]");
        string Log(string id)
        {
            var directory = Directory.CreateDirectory(Path.Combine(sessions, "project", id)).FullName;
            var path = Path.Combine(directory, "session.v4.jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(new { type = "session", id, version = 4, createdAt = 1767225600000L, cwd = root }) + "\n");
            return path;
        }
        var selected = Log("release-selected");
        var keep = Log("release-keep");
        var sentinel = Path.Combine(root, "outside-sentinel");
        File.WriteAllText(sentinel, "protected synthetic sentinel");
        var protectedHashes = new[] { keep, sentinel }.ToDictionary(x => x, Hash);
        var workspace = Path.Combine(storages, "workspace.json");
        File.WriteAllText(workspace, JsonSerializer.Serialize(new
        {
            unit = new { name = "workspace", version = 2 },
            global = new { initialized = true, workspaceIds = new[] { "work" }, defaultWorkspaceId = "work", archivedSessionIds = new[] { "release-selected" }, pinnedSessionIds = new[] { "release-keep" } },
            tables = new { workspaces = new Dictionary<string, object> { ["work"] = new { path = root, title = "Synthetic", sessionIds = new[] { "release-selected", "release-keep" }, createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" } } },
        }));
        var original = File.ReadAllBytes(workspace);
        var inheritedHome = Environment.GetEnvironmentVariable("DSH_HOME");
        try
        {
            // Current test process only. All write targets were created above under this owned fixture.
            Environment.SetEnvironmentVariable("DSH_HOME", home, EnvironmentVariableTarget.Process);
            Console.WriteLine("Installed-package public service path; SYNTHETIC DATA; simulated inactive process probe.");
            Check("production binding accepts installed package and synthetic inherited home", WorkspaceProductionGate.Read(home) is not null);
            var inventory = new InventoryService(new UiScanSource()).Scan(sessions).Sessions.Where(x => x.SessionId == "release-selected").ToArray();
            var plan = new SessionCleanupPreparation().Build(sessions, inventory).Plan!;
            var before = new PreCleanupAuditor().Audit(sessions, ["release-selected"], plan);
            var inactive = new HarnessProcessProbe(() => []);
            var coordinator = new WorkspaceSyncCoordinator(inactive); // Public constructor, no synthetic release bypass.
            Check("public coordinator enables known storage binding", coordinator.GetOption(plan, before, backups).Available);
            File.WriteAllText(Path.Combine(profile, "cordis.patch.yml"), "- id: storage-domain\n  config: {backend: unknown}\n");
            Check("public coordinator rejects unknown backend before deletion", !coordinator.GetOption(plan, before, backups).Available);
            File.WriteAllText(Path.Combine(profile, "cordis.patch.yml"), "[]");
            var active = new HarnessProcessProbe(() => [new ProcessSnapshot(1234, "DeepSeek Harness", @"C:\Harness\DeepSeek Harness.exe", true)]);
            Check("public coordinator retains active-process gate", !new WorkspaceSyncCoordinator(active).GetOption(plan, before, backups).Available);
            var execution = new CleanupExecutor(inactive).Execute(plan, null, null, sessions, true);
            Check("physical executor touches only synthetic selected log", execution.AllSucceeded && !File.Exists(selected));
            var uncheckedAttempt = coordinator.Run(plan, before, execution, false, backups);
            Check("unchecked public path leaves metadata and independent backups untouched", uncheckedAttempt.Outcome.State == WorkspaceSyncState.NotRequested
                && original.SequenceEqual(File.ReadAllBytes(workspace)) && Directory.GetFileSystemEntries(backups).Length == 0);
            var attempt = coordinator.Run(plan, before, execution, true, backups);
            Check("public coordinator applies validated release without internal writer bypass", attempt.Outcome.State == WorkspaceSyncState.Applied);
            var references = new WorkspaceReferenceReader().Read(home).References;
            Check("only eligible selected references removed", !references.Any(x => x.SessionId == "release-selected")
                && references.Count(x => x.SessionId == "release-keep") == 2);
            Check("mandatory raw backup verified and byte-exact", attempt.Backup is not null && original.SequenceEqual(File.ReadAllBytes(attempt.Backup.RawPath))
                && new WorkspaceBackupService(inactive).Verify(attempt.Plan!, attempt.Backup).Success);
            foreach (var item in protectedHashes) Check("public path protected hash " + Path.GetFileName(item.Key), Hash(item.Key) == item.Value);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", inheritedHome, EnvironmentVariableTarget.Process);
            Console.WriteLine("INSTALLED_STORAGE_FIXTURE " + root);
        }
    }
}
