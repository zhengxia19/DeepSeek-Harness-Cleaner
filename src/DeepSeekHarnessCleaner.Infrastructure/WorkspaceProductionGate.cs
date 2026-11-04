using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Release recognition plus data-only storage binding validation; no Harness code is executed.</summary>
internal static class WorkspaceProductionGate
{
    internal static string? Read(string dataHome)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            var home = Path.GetFullPath(dataHome);
            var inherited = Environment.GetEnvironmentVariable("DSH_HOME");
            var account = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var selected = string.IsNullOrWhiteSpace(inherited) ? Path.Combine(account, ".dsh") : inherited;
            if (selected == "~") selected = account;
            else if (selected.StartsWith("~/", StringComparison.Ordinal) || selected.StartsWith("~\\", StringComparison.Ordinal))
                selected = Path.Combine(account, selected[2..]);
            if (!string.Equals(home.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return null;
            var archive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "DeepSeek Harness", "resources", "app.asar");
            if (!WorkspaceReleaseReader.MatchesReviewedRelease(archive)) return null;
            return ReadConfiguration(home, entry => WorkspaceReleaseReader.ReadPacked(archive, entry), ReadLocal);
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    // Pure IO seam for synthetic package/profile fixtures. Does not bypass release or root checks in Read.
    internal static string? ReadConfiguration(string home, Func<string, byte[]?> packaged, Func<string, byte[]?> local)
    {
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = 0;
            byte[]? Observe(string name, byte[]? bytes)
            {
                if (bytes is { Length: > 1024 * 1024 } || (total += bytes?.Length ?? 0) > 16 * 1024 * 1024)
                    throw new InvalidDataException();
                digest.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
                digest.AppendData(bytes is null ? [0] : [1]);
                if (bytes is not null) digest.AppendData(SHA256.HashData(bytes));
                return bytes;
            }
            byte[]? Package(string name) => Observe("package:" + name, packaged(name));
            byte[]? Local(string name) => Observe("local:" + name, local(name));
            using var profile = Document(Local(Path.Combine(home, "profiles", "desktop", "package.json")));
            var bundles = profile.RootElement.GetProperty("dsh").GetProperty("profile").GetProperty("bundles");
            if (bundles.ValueKind != JsonValueKind.Array || bundles.GetArrayLength() > 64) return null;
            var layers = new List<string>();
            foreach (var bundle in bundles.EnumerateArray())
            {
                var name = bundle.GetString() ?? throw new InvalidDataException();
                if (!Regex.IsMatch(name, @"^(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant)
                    || name.Contains("..", StringComparison.Ordinal)) return null;
                var packageRoot = "dsh/node_modules/" + name;
                var manifestBytes = Package(packageRoot + "/package.json");
                string? localRoot = null;
                if (manifestBytes is null)
                {
                    for (var dir = Path.Combine(home, "profiles", "desktop"); dir is not null; dir = Path.GetDirectoryName(dir))
                    {
                        var candidate = Path.Combine(dir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
                        manifestBytes = Local(Path.Combine(candidate, "package.json"));
                        if (manifestBytes is null) continue;
                        localRoot = candidate;
                        break;
                    }
                }
                using var manifest = Document(manifestBytes);
                if (manifest.RootElement.GetProperty("name").GetString() != name || !Compatible(manifest.RootElement)) return null;
                var patch = manifest.RootElement.GetProperty("dsh").GetProperty("bundle").GetProperty("patch");
                var paths = patch.ValueKind == JsonValueKind.String ? [patch.GetString()!]
                    : patch.EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (paths.Length > 32) return null;
                foreach (var path in paths)
                {
                    if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\')
                        || path.Contains(':') || path.Split('/').Any(x => x == "..")) return null;
                    var relative = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
                    var bytes = localRoot is null ? Package(packageRoot + "/" + relative)
                        : Local(Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                    layers.Add(Utf8(bytes));
                }
            }
            foreach (var file in new[] { Path.Combine(home, "profiles", "desktop", "cordis.patch.yml"), Path.Combine(home, "cordis.patch.yml") })
            {
                var bytes = Local(file);
                if (bytes is not null) layers.Add(Utf8(bytes));
            }
            // Core plugins must resolve to the packaged release, with satisfiable compatibility metadata.
            foreach (var core in new[] { "storage", "storage-json", "storage-domain", "workspace" })
            {
                using var manifest = Document(Package("dsh/node_modules/@deepseek-ai/dsh-" + core + "/package.json"));
                if (manifest.RootElement.GetProperty("name").GetString() != "@deepseek-ai/dsh-" + core
                    || manifest.RootElement.GetProperty("version").GetString() != WorkspaceReleaseReader.Release
                    || !Compatible(manifest.RootElement)) return null;
            }
            return WorkspaceStoragePolicy.Allows(layers) ? Convert.ToHexString(digest.GetHashAndReset()) : null;
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    private static byte[]? ReadLocal(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException();
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static JsonDocument Document(byte[]? bytes)
    {
        if (bytes is null) throw new InvalidDataException();
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new InvalidDataException();
    }
    private static string Utf8(byte[]? bytes) => bytes is null ? throw new InvalidDataException() : new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');

    private static bool Compatible(JsonElement manifest)
    {
        if (manifest.TryGetProperty("engines", out var engines) && engines.TryGetProperty("dsh", out var engine)) return Range(engine.GetString());
        if (!manifest.TryGetProperty("peerDependencies", out var peers)) return true;
        return peers.EnumerateObject().Where(x => x.Name == "@deepseek-ai/dsh" || x.Name.StartsWith("@deepseek-ai/dsh-", StringComparison.Ordinal))
            .All(x => x.Value.GetString() is "workspace:^" or "workspace:~" or "workspace:*" || Range(x.Value.GetString()));
    }
    // Exact reviewed constraints for this prerelease; unknown ranges/exemptions cannot authorize writes.
    private static bool Range(string? range) => range?.Trim() is "0.2.1-rc.2" or "^0.1.7-rc.1 || ^0.2.0-rc.1" or ">=0.2.1-rc.2 <0.2.2";
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException
        or OverflowException or NotSupportedException or System.Security.SecurityException;
}
