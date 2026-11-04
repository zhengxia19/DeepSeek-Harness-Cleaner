using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.UI;

internal static partial class Program
{
    private static readonly JsonSerializerOptions CrashJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    private sealed record CrashInput(WorkspaceMetadataPlan Plan, WorkspaceBackupEvidence Backup);

    private static int RunWorkspaceCrashChild(string[] args)
    {
        try
        {
            if (args.Length != 3 || !Enum.TryParse<WorkspaceWritePoint>(args[2], out var stop) || !Enum.IsDefined(stop)) return 2;
            var root = Path.GetFullPath(args[1]);
            var directory = new DirectoryInfo(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures"));
            if (!string.Equals(directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase)
                || !directory.Name.StartsWith("fx-", StringComparison.Ordinal) || !Guid.TryParseExact(directory.Name[3..], "N", out _)) return 2;
            var inputPath = Path.Combine(root, "crash-input.json");
            if (!new PathGuard().Check(root, inputPath).IsAllowed || new FileInfo(inputPath).Length > 1024 * 1024) return 2;
            var input = JsonSerializer.Deserialize<CrashInput>(File.ReadAllBytes(inputPath), CrashJson)!;
            var home = Path.Combine(root, ".dsh");
            if (!string.Equals(input.Plan.DataHome, home, StringComparison.OrdinalIgnoreCase)) return 2;
            var writer = new WorkspaceMetadataWriter(home, new HarnessProcessProbe(() => []), point =>
            {
                if (point != stop) return;
                Console.WriteLine("CRASH_READY " + input.Plan.OperationId.ToString("N") + " " + point);
                Console.Out.Flush();
                // Parent owns this process handle and terminates it while native leases and stack frames are live.
                // No managed cancellation/exception/unwind or finally block completes the operation.
                _ = Console.ReadLine();
                throw new InvalidOperationException("Parent must terminate the child, never resume it.");
            });
            var preparation = writer.Prepare(input.Plan, input.Backup);
            if (!preparation.Success) { Console.WriteLine("CRASH_PREPARE_BLOCKED " + preparation.ErrorCode); return 3; }
            _ = writer.Apply(preparation.Preparation!);
            return 4; // The requested barrier was not reached: never count this as a crash test.
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); return 5; }
    }

    private static int RunWorkspaceCrashChecks()
    {
        foreach (var point in Enum.GetValues<WorkspaceWritePoint>())
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dshcleaner-fixtures", "fx-" + Guid.NewGuid().ToString("N"))).FullName;
            var home = Path.Combine(root, ".dsh");
            var sessions = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
            var storages = Directory.CreateDirectory(Path.Combine(home, "storages")).FullName;
            Directory.CreateDirectory(Path.Combine(home, "attachments"));
            var backupRoot = Directory.CreateDirectory(Path.Combine(root, "metadata-backups")).FullName;
            var output = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(".artifacts", "FinalAcceptance", "crash-" + point + "-" + Guid.NewGuid().ToString("N")))).FullName;
            try
            {
                string WriteLog(string id)
                {
                    var dir = Directory.CreateDirectory(Path.Combine(sessions, "project", id)).FullName;
                    var path = Path.Combine(dir, "session.v4.jsonl");
                    File.WriteAllText(path, JsonSerializer.Serialize(new { type = "session", id, version = 4, createdAt = 1767225600000L, cwd = root }) + "\n");
                    return path;
                }
                var selected = WriteLog("crash-selected");
                var keep = WriteLog("crash-keep");
                var sentinel = Path.Combine(root, "outside-sentinel");
                File.WriteAllText(sentinel, "synthetic protected sentinel");
                var hashes = new[] { keep, sentinel }.ToDictionary(x => x, Hash);
                var workspace = Path.Combine(storages, "workspace.json");
                File.WriteAllText(workspace, JsonSerializer.Serialize(new
                {
                    unit = new { name = "workspace", version = 2 },
                    global = new { initialized = true, workspaceIds = new[] { "work" }, defaultWorkspaceId = "work", archivedSessionIds = new[] { "crash-selected" }, pinnedSessionIds = new[] { "crash-keep" } },
                    tables = new { workspaces = new Dictionary<string, object> { ["work"] = new { path = root, title = "Synthetic", sessionIds = new[] { "crash-selected", "crash-keep" }, createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-01T00:00:00Z" } } },
                }));
                var original = File.ReadAllBytes(workspace);
                var inventory = new InventoryService(new UiScanSource()).Scan(sessions).Sessions.Where(x => x.SessionId == "crash-selected").ToArray();
                var cleanup = new SessionCleanupPreparation().Build(sessions, inventory).Plan!;
                var before = new PreCleanupAuditor().Audit(sessions, ["crash-selected"], cleanup);
                var execution = new CleanupExecutor(new HarnessProcessProbe(() => [])).Execute(cleanup, null, null, sessions, true);
                Check("real synthetic physical deletion before child " + point, execution.AllSucceeded && !File.Exists(selected));
                var after = new PostCleanupAuditor().Audit(sessions, cleanup, before);
                var proposal = new WorkspaceMetadataProposal().Build(cleanup, before,
                    execution.Outcomes.Select(x => new WorkspacePhysicalOutcome(x.AbsolutePath, WorkspacePhysicalStatus.Deleted)).ToArray(), after);
                var backup = new WorkspaceBackupService(new HarnessProcessProbe(() => [])).Create(proposal, backupRoot).Backup!;
                var inputPath = Path.Combine(root, "crash-input.json");
                File.WriteAllBytes(inputPath, JsonSerializer.SerializeToUtf8Bytes(new CrashInput(proposal, backup), CrashJson));
                var executable = Path.Combine(AppContext.BaseDirectory, "DeepSeekHarnessCleaner.WindowsTests.exe");
                Check("owned child executable exists " + point, File.Exists(executable));
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("--workspace-crash-child");
                start.ArgumentList.Add(root);
                start.ArgumentList.Add(point.ToString());
                using var child = Process.Start(start)!;
                var reached = false;
                try
                {
                    var ready = child.StandardOutput.ReadLineAsync();
                    Check("child barrier wait completed " + point, ready.Wait(TimeSpan.FromSeconds(15)));
                    reached = ready.Result == "CRASH_READY " + proposal.OperationId.ToString("N") + " " + point;
                    Check("exact live child reached requested barrier " + point, reached && !child.HasExited);
                    var pid = child.Id;
                    child.Kill(entireProcessTree: false); // Only our Process.Start handle; never enumerate/stop Harness.
                    Check("owned child forcibly exited " + point, child.WaitForExit(10000) && child.HasExited);
                    File.WriteAllText(Path.Combine(output, "process-proof.json"), JsonSerializer.Serialize(new
                    {
                        stage = point.ToString(),
                        pid,
                        barrier = ready.Result,
                        killRequested = true,
                        exited = child.HasExited,
                        exitCode = child.ExitCode,
                        stderr = child.StandardError.ReadToEnd(),
                        observedUtc = DateTimeOffset.UtcNow,
                    }, CrashJson));
                    Check("child did not exit normally " + point, child.ExitCode != 0);
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: false); child.WaitForExit(10000); }
                }
                Check("marker or durable completion survived real termination " + point,
                    point == WorkspaceWritePoint.OutcomeRecorded
                        ? !File.Exists(Path.Combine(storages, ".dshcleaner-workspace-sync.lock")) && File.Exists(Path.Combine(Path.GetDirectoryName(backup.RawPath)!, "write-applied.json"))
                        : File.Exists(Path.Combine(storages, ".dshcleaner-workspace-sync.lock")));
                // Load the known operation from disk again, using a new service with no child/preparation state.
                var restored = JsonSerializer.Deserialize<CrashInput>(File.ReadAllBytes(inputPath), CrashJson)!;
                var recovery = new WorkspaceRecoveryService(home, new HarnessProcessProbe(() => []));
                var inspection = recovery.Inspect(restored.Plan, restored.Backup);
                var applied = point is WorkspaceWritePoint.Replaced or WorkspaceWritePoint.Verified or WorkspaceWritePoint.OutcomeRecorded;
                var expected = point == WorkspaceWritePoint.OutcomeRecorded ? WorkspaceRecoveryState.ResolvedApplied : applied ? WorkspaceRecoveryState.AppliedVerified : point == WorkspaceWritePoint.OriginalRetained
                    ? WorkspaceRecoveryState.OriginalRetained : WorkspaceRecoveryState.OriginalUnchanged;
                Check("disk-only inspection matches interruption state " + point, inspection.State == expected);
                var resolved = recovery.Resolve(restored.Plan, restored.Backup);
                Check("recovery after force termination verified " + point, resolved.State == (applied ? WorkspaceRecoveryState.ResolvedApplied : WorkspaceRecoveryState.ResolvedOriginal));
                Check("original raw backup survived " + point, original.SequenceEqual(File.ReadAllBytes(restored.Backup.RawPath)));
                Check("deleted log was not restored " + point, !File.Exists(selected));
                var interrupted = new WorkspaceSyncAttempt(new(cleanup.PlanId, restored.Plan.OperationId, WorkspaceSyncState.RecoveryRequired,
                    "SYNC_PROCESS_TERMINATED", restored.Plan.ReferencesToRemove.Count, null, Path.GetDirectoryName(restored.Backup.RawPath), DateTimeOffset.UtcNow),
                    restored.Plan, restored.Backup, after, backupRoot);
                var retry = new WorkspaceSyncCoordinator(home, new HarnessProcessProbe(() => [])).Retry(cleanup, before, execution, interrupted);
                Check("fresh retry reconciles remaining work " + point, retry.Outcome.State == (applied ? WorkspaceSyncState.NoOp : WorkspaceSyncState.Applied));
                Check("only selected refs removed after retry " + point, !new WorkspaceReferenceReader().Read(home).References.Any(x => x.SessionId == "crash-selected")
                    && new WorkspaceReferenceReader().Read(home).References.Count(x => x.SessionId == "crash-keep") == 2);
                foreach (var hash in hashes) Check("protected hash after forced exit " + point + " " + Path.GetFileName(hash.Key), Hash(hash.Key) == hash.Value);
                File.WriteAllText(Path.Combine(output, "recovery-proof.json"), JsonSerializer.Serialize(new
                {
                    stage = point.ToString(),
                    inspection.State,
                    resolved = resolved.State,
                    retry = retry.Outcome.State,
                    originalBackupVerified = new WorkspaceBackupService().Verify(restored.Plan, restored.Backup).Success,
                    selectedLogAbsent = !File.Exists(selected),
                    protectedHashes = hashes.Values.ToArray(),
                    fixture = root,
                }, CrashJson));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("CRASH_FIXTURE " + root + " " + output); return 1; }
            Console.WriteLine("CRASH_FIXTURE " + root + " " + output);
        }
        return 0;
    }
}
