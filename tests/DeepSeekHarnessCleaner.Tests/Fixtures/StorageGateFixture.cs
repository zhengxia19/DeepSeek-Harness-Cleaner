using System.Text;
using System.Text.Json;
using DeepSeekHarnessCleaner.Infrastructure;

namespace DeepSeekHarnessCleaner.Tests.Fixtures;

internal sealed class StorageGateFixture(string home)
{
    internal Dictionary<string, byte[]> Packages { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, byte[]> Local { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string UserPatch => Path.Combine(home, "profiles", "desktop", "cordis.patch.yml");
    internal const string BundleManifest = "dsh/node_modules/test-bundle/package.json";
    internal const string BundlePatch = "dsh/node_modules/test-bundle/cordis.patch.yml";

    internal void Initialize()
    {
        Local[Path.Combine(home, "profiles", "desktop", "package.json")] = Json(new { dsh = new { profile = new { bundles = new[] { "test-bundle" } } } });
        Packages[BundleManifest] = Json(new { name = "test-bundle", dsh = new { bundle = new { patch = "./cordis.patch.yml" } } });
        Packages[BundlePatch] = Encoding.UTF8.GetBytes(WorkspaceStoragePolicyTests.Default);
        foreach (var core in new[] { "storage", "storage-json", "storage-domain", "workspace" })
        {
            var name = "@deepseek-ai/dsh-" + core;
            Packages["dsh/node_modules/" + name + "/package.json"] = Json(new { name, version = WorkspaceReleaseReader.Release });
        }
    }

    internal string? Read() => WorkspaceProductionGate.ReadConfiguration(home, key => Packages.GetValueOrDefault(key), key => Local.GetValueOrDefault(key));
    internal static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
}
