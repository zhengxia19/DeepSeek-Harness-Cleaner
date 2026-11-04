using System.Security.Cryptography;
using DeepSeekHarnessCleaner.Application;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class SessionResidueInventoryTests
{
    private static string Log(FixtureRoot root, string id, string? attachmentId = null, string project = "project") =>
        root.Factory.WriteSessionFile(project, id, "session.v4.jsonl", FixtureFactory.Utf8(
            "{\"type\":\"session\",\"version\":4,\"id\":\"" + id + "\",\"createdAt\":1767225600000}\n"
            + (attachmentId is null ? "" : "{\"type\":\"user/message\",\"data\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"attachment\":{\"attachmentId\":\"" + attachmentId + "\"}}]}}\n")));

    private static (string Path, string Id) Attachment(FixtureRoot root)
    {
        byte[] content = [10, 20, 30];
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var path = root.Factory.WriteFile(root.AttachmentsRoot, Path.Combine("v1", "objects", hash[..2], hash), content);
        return (path, "sha256:" + hash);
    }

    private static CleanupTarget Target(string path, CleanupCategory category, string? owner) =>
        new(path, category, owner, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), null);

    [Fact]
    public void LogAndCacheTargets_AreSeparateFromRetainedSharedAttachmentsAndMetadata()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var attachment = Attachment(root);
        var a = Log(root, WorkspaceFixture.ArchivedId, attachment.Id);
        Log(root, WorkspaceFixture.PinnedId, attachment.Id);
        new WorkspaceFixture(root).Write("valid");
        root.Factory.WriteProjectionCache(WorkspaceFixture.ArchivedId);
        var cache = Path.Combine(root.StoragesRoot, "session_projcache", "sessions", WorkspaceFixture.ArchivedId + ".json");
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var hashes = Directory.GetFiles(root.Root, "*", SearchOption.AllDirectories).Append(sentinel).ToDictionary(x => x,
            x => SHA256.HashData(File.ReadAllBytes(x)));
        var targets = new[] { Target(a, CleanupCategory.SessionLog, WorkspaceFixture.ArchivedId), Target(cache, CleanupCategory.DedicatedCache, WorkspaceFixture.ArchivedId) };
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId], targets);
        var entry = Assert.Single(result.Sessions);
        Assert.Equal(2, entry.Items.Count(x => x.Scope == SessionResidueScope.TargetFile));
        Assert.Contains(entry.Items, x => x.Kind == SessionResidueKind.SessionLog && x.EvidencePath == a && x.Scope == SessionResidueScope.TargetFile);
        Assert.Contains(entry.Items, x => x.Kind == SessionResidueKind.DedicatedCache && x.EvidencePath == cache && x.Scope == SessionResidueScope.TargetFile);
        var retained = Assert.Single(entry.Items, x => x.Kind == SessionResidueKind.Attachment);
        Assert.Equal(SessionResidueScope.NotInScope, retained.Scope);
        Assert.Equal(2, retained.RelatedSessionIds.Count);
        Assert.Equal("SHARED_ATTACHMENT", retained.Reason);
        Assert.All(entry.Items.Where(x => x.Kind == SessionResidueKind.WorkspaceReference), x => Assert.Equal(SessionResidueScope.NotInScope, x.Scope));
        Assert.Contains(result.UnattributedItems, x => x.Kind == SessionResidueKind.WholeCache && x.Scope == SessionResidueScope.NotInScope);
        Assert.True(result.HasUnknowns); // Attachment coverage is deliberately incomplete, even with positive references.
        Assert.Contains(result.Issues, x => x.Code == "ATTACHMENT_REFERENCE_COVERAGE_UNKNOWN");
        Assert.Equal(2, targets.Length);
        foreach (var file in hashes) Assert.Equal(file.Value, SHA256.HashData(File.ReadAllBytes(file.Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachmentIsATargetOnlyWhenIndependentlySelected(bool selected)
    {
        using var root = FixtureRoot.Create();
        var attachment = Attachment(root);
        Log(root, WorkspaceFixture.ArchivedId, attachment.Id);
        new WorkspaceFixture(root).Write("valid");
        var targets = selected ? new[] { Target(attachment.Path, CleanupCategory.Attachment, null) } : Array.Empty<CleanupTarget>();
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId], targets);
        var item = Assert.Single(Assert.Single(result.Sessions).Items, x => x.Kind == SessionResidueKind.Attachment);
        Assert.Equal(selected ? SessionResidueScope.TargetFile : SessionResidueScope.NotInScope, item.Scope);
        Assert.True(File.Exists(attachment.Path));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unknown-version")]
    [InlineData("pending-create")]
    public void UnknownWorkspaceCannotBeReportedAsKnownNoReferences(string scenario)
    {
        using var root = FixtureRoot.Create();
        Log(root, WorkspaceFixture.ArchivedId);
        new WorkspaceFixture(root).Write(scenario);
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId], []);
        Assert.NotEqual(WorkspaceReferenceStatus.Known, result.Workspace.Status);
        Assert.True(result.HasUnknowns);
        Assert.Contains(result.Issues, x => x.Code.StartsWith("WORKSPACE_", StringComparison.Ordinal));
        Assert.DoesNotContain(Assert.Single(result.Sessions).Items, x => x.Kind == SessionResidueKind.WorkspaceReference);
    }

    [Fact]
    public void UnlinkedAndDanglingAttachmentsRemainUnknown_NotOrphansOrInventedFiles()
    {
        using var root = FixtureRoot.Create();
        var local = Attachment(root);
        var missingId = "sha256:" + new string('f', 64);
        Log(root, WorkspaceFixture.ArchivedId, missingId);
        new WorkspaceFixture(root).Write("valid");
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId], []);
        Assert.Contains(result.UnattributedItems, x => x.Kind == SessionResidueKind.Attachment && x.EvidencePath == local.Path && x.Scope == SessionResidueScope.UnknownReference);
        var missing = Assert.Single(Assert.Single(result.Sessions).Items, x => x.Kind == SessionResidueKind.Attachment);
        Assert.Equal(missingId, missing.RelatedId);
        Assert.Equal(SessionResidueScope.UnknownReference, missing.Scope);
        Assert.Equal("ATTACHMENT_FILE_MISSING", missing.Reason);
        Assert.Equal(root.AttachmentsRoot, missing.EvidencePath); // Container evidence, never a guessed file path.
        Assert.True(result.HasUnknowns);
    }

    [Fact]
    public void MetadataWithoutLogs_IsRetainedEvidence_AndCacheOwnershipStaysUnproven()
    {
        using var root = FixtureRoot.Create();
        new WorkspaceFixture(root).Write("valid");
        root.Factory.WriteProjectionCache(WorkspaceFixture.ArchivedId);
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId], []);
        var entry = Assert.Single(result.Sessions);
        Assert.DoesNotContain(entry.Items, x => x.Kind == SessionResidueKind.SessionLog);
        Assert.Contains(entry.Items, x => x.Kind == SessionResidueKind.WorkspaceReference && x.Scope == SessionResidueScope.NotInScope);
        Assert.Contains(result.UnattributedItems, x => x.Kind == SessionResidueKind.DedicatedCache && x.Scope == SessionResidueScope.UnknownReference);
        Assert.True(result.HasUnknowns);
    }

    [Fact]
    public void BadIdentityDuplicateIdentityAndWrongPlanOwner_NeverCreateTrustedTargets()
    {
        using var root = FixtureRoot.Create();
        var a = Log(root, WorkspaceFixture.ArchivedId);
        Log(root, WorkspaceFixture.ArchivedId, project: "other-project");
        var b = Log(root, "bad-identity");
        File.WriteAllText(b, "{\"type\":\"session\",\"version\":4,\"id\":\"someone-else\",\"createdAt\":1767225600000}\n");
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId, "bad-identity"],
            [Target(a, CleanupCategory.SessionLog, WorkspaceFixture.ArchivedId), Target(b, CleanupCategory.SessionLog, "bad-identity")]);
        Assert.All(result.Sessions.SelectMany(x => x.Items).Where(x => x.Kind == SessionResidueKind.SessionLog), x => Assert.Equal(SessionResidueScope.UnknownReference, x.Scope));
        Assert.Contains(result.Issues, x => x.Code == "SESSION_IDENTITY_DUPLICATE");
        Assert.Contains(result.Issues, x => x.Code == "SESSION_IDENTITY_UNKNOWN");
        Assert.DoesNotContain(result.Sessions.SelectMany(x => x.Items), x => x.Scope == SessionResidueScope.TargetFile);
    }

    [Fact]
    public void InvalidAndUnselectedPlanTargets_AreUnlinked_NoScopeExpansion()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var a = Log(root, WorkspaceFixture.ArchivedId);
        var b = Log(root, WorkspaceFixture.PinnedId);
        var sentinel = outside.Factory.WriteFile(outside.Root, "sentinel.txt", FixtureFactory.Utf8("protected"));
        var hash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId],
            [Target(a, CleanupCategory.DedicatedCache, WorkspaceFixture.ArchivedId),
             Target(b, CleanupCategory.SessionLog, WorkspaceFixture.PinnedId),
             Target(sentinel, CleanupCategory.SessionLog, WorkspaceFixture.ArchivedId)]);
        Assert.DoesNotContain(result.Sessions.SelectMany(x => x.Items), x => x.Scope == SessionResidueScope.TargetFile);
        Assert.Equal(2, result.UnattributedItems.Count(x => x.Kind == SessionResidueKind.UnlinkedPlanTarget));
        Assert.Contains(result.Issues, x => x.Code == PathGuard.PathEscapeErrorCode);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    [Fact]
    public void ReparseLogAndPlanTarget_AreUnknown_OutsideBytesProtected()
    {
        using var root = FixtureRoot.Create();
        using var outside = FixtureRoot.Create();
        var target = Log(outside, WorkspaceFixture.ArchivedId);
        var original = File.ReadAllBytes(target);
        var directory = Path.Combine(root.SessionsRoot, "project", WorkspaceFixture.ArchivedId);
        Directory.CreateDirectory(directory);
        var link = Path.Combine(directory, "session.v4.jsonl");
        try
        {
            File.CreateSymbolicLink(link, target);
            var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId],
                [Target(link, CleanupCategory.SessionLog, WorkspaceFixture.ArchivedId)]);
            Assert.True(result.HasUnknowns);
            Assert.Contains(result.Issues, x => x.Code == PathGuard.PathReparseErrorCode);
            Assert.DoesNotContain(result.Sessions.SelectMany(x => x.Items), x => x.Scope == SessionResidueScope.TargetFile);
            Assert.Equal(original, File.ReadAllBytes(target));
        }
        finally { if (File.Exists(link)) File.Delete(link); }
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void SelectionAndCancellation_AreExplicit()
    {
        using var root = FixtureRoot.Create();
        var result = new SessionResidueInventory().Scan(root.DshHome, [WorkspaceFixture.ArchivedId, WorkspaceFixture.ArchivedId, " "], []);
        Assert.Single(result.Sessions);
        Assert.Contains(result.Issues, x => x.Code == "SELECTION_INVALID_OR_DUPLICATE");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SessionResidueInventory().Scan(root.DshHome, [], [], cts.Token));
        var invalid = new SessionResidueInventory().Scan("relative-root", [WorkspaceFixture.ArchivedId], []);
        Assert.Contains(invalid.Issues, x => x.Code == "ROOT_INVALID");
        Assert.True(invalid.HasUnknowns);
    }
}
