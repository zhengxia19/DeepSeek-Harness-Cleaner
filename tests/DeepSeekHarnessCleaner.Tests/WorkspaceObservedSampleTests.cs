using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekHarnessCleaner.Domain;
using DeepSeekHarnessCleaner.Infrastructure;
using DeepSeekHarnessCleaner.Tests.Fixtures;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceObservedSampleTests
{
    [Fact]
    public void ObservedRedactedSampleHasVerifiableProvenance_AndCompleteReadOnlyIndex()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "observed-workspace-v2-redacted.json");
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(samplePath, ".provenance.json")));
        var proof = provenance.RootElement;
        Assert.True(proof.GetProperty("sourceStableAcrossRead").GetBoolean());
        Assert.True(proof.GetProperty("sourceIdTypesAndArrayUniquenessValidated").GetBoolean());
        Assert.False(proof.GetProperty("sourceMutationPerformed").GetBoolean());
        Assert.False(proof.GetProperty("producerReleaseMappingVerified").GetBoolean());
        var bytes = File.ReadAllBytes(samplePath);
        Assert.Equal(proof.GetProperty("sampleSha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var root = FixtureRoot.Create();
        var copied = root.Factory.WriteFile(root.StoragesRoot, "workspace.json", bytes);
        var index = new WorkspaceReferenceReader().Read(root.DshHome);
        Assert.True(index.Status == WorkspaceReferenceStatus.Known, index.Reason);
        Assert.Equal(20, index.References.Count(x => x.Kind == WorkspaceReferenceKind.Archived));
        Assert.Equal(0, index.References.Count(x => x.Kind == WorkspaceReferenceKind.Pinned));
        Assert.Equal(51, index.References.Count(x => x.Kind == WorkspaceReferenceKind.WorkspaceMember));
        Assert.Equal(55, index.References.Select(x => x.SessionId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(index.References, x => Assert.Matches("^SAMPLE_SESSION_[0-9]{4}$", x.SessionId));
        Assert.Equal(bytes, File.ReadAllBytes(copied));
    }

    [Fact]
    public void RedactionKeepsOnlyMaskedIdentifiersPathsTitlesAndExplicitlyReplacedDates()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "observed-workspace-v2-redacted.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(samplePath));
        var root = document.RootElement;
        var global = root.GetProperty("global");
        Assert.All(global.GetProperty("workspaceIds").EnumerateArray(), x => Assert.Matches("^SAMPLE_WORKSPACE_[0-9]{4}$", x.GetString()!));
        foreach (var record in root.GetProperty("tables").GetProperty("workspaces").EnumerateObject())
        {
            Assert.Matches("^SAMPLE_WORKSPACE_[0-9]{4}$", record.Name);
            Assert.Equal("C:\\sanitized\\" + record.Name, record.Value.GetProperty("path").GetString());
            Assert.Equal("Sanitized " + record.Name, record.Value.GetProperty("title").GetString());
            Assert.Equal("2000-01-01T00:00:00.000Z", record.Value.GetProperty("createdAt").GetString());
            Assert.Equal("2000-01-01T00:00:00.000Z", record.Value.GetProperty("updatedAt").GetString());
        }
    }
}
