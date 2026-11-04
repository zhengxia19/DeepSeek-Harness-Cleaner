using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Data-only composition of reviewed release patch semantics. Never evaluates !!js.</summary>
internal static class WorkspaceStoragePolicy
{
    private static readonly string[] Core = ["storage", "storage-json", "storage-domain", "workspace"];

    internal static bool Allows(IEnumerable<string> orderedLayers)
    {
        try
        {
            var rows = new List<YamlMappingNode>();
            var ids = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
            foreach (var layer in orderedLayers)
                foreach (var patch in Sequence(Parse(layer))) Apply(patch, rows, ids);
            var all = Flatten(rows, false).ToArray();
            if (all.Any(x => Text(Get(x.Row, "name")) is "cordis:include" or "@deepseek-ai/cordis-plugin-include")) return false;
            foreach (var id in Core)
            {
                var name = "@deepseek-ai/dsh-" + id;
                var matches = all.Where(x => Text(Get(x.Row, "name")) == name).ToArray();
                if (matches.Length != 1 || matches[0].Disabled) return false;
                var row = matches[0].Row;
                if (row.Children.Keys.Any(k => Text(k) is not ("id" or "name" or "config" or "disabled"))) return false;
                if (Get(row, "group") is not null) return false;
                var config = Get(row, "config");
                if (id is "storage" or "workspace")
                {
                    if (config is not null && (config is not YamlMappingNode empty || empty.Children.Count != 0)) return false;
                }
                else if (id == "storage-json")
                {
                    if (config is not YamlMappingNode map || map.Children.Count != 1) return false;
                    if (Get(map, "root") is not YamlScalarNode root || root.Tag.ToString() != "tag:yaml.org,2002:js"
                        || root.Value is not ("dshHomePath('storages')" or "dshHomePath(\"storages\")")) return false;
                }
                else
                {
                    if (config is not YamlMappingNode map || Text(Get(map, "backend")) != "json") return false;
                    if (map.Children.Keys.Any(k => Text(k) is not ("backend" or "routes"))) return false;
                    if (Get(map, "routes") is { } routes)
                    {
                        if (routes is not YamlMappingNode routeMap) return false;
                        if (Get(routeMap, "workspace") is { } route && Text(route) != "json") return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is YamlException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static YamlNode Parse(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException();
        // Bound structure before representation construction. Aliases/anchors cannot expand or form cycles.
        var parser = new Parser(new StringReader(text));
        var depth = 0;
        var nodes = 0;
        while (parser.MoveNext())
        {
            if (++nodes > 50000 || parser.Current is AnchorAlias) throw new InvalidDataException();
            if (parser.Current is NodeEvent node && !node.Anchor.IsEmpty) throw new InvalidDataException();
            if (parser.Current is NodeEvent tagged && !tagged.Tag.IsEmpty
                && tagged.Tag.ToString() is not ("tag:yaml.org,2002:str" or "tag:yaml.org,2002:bool"
                    or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:null"
                    or "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq" or "tag:yaml.org,2002:js")) throw new InvalidDataException();
            if (parser.Current is MappingStart mapping && !mapping.Tag.IsEmpty && mapping.Tag.ToString() != "tag:yaml.org,2002:map"
                || parser.Current is SequenceStart sequence && !sequence.Tag.IsEmpty && sequence.Tag.ToString() != "tag:yaml.org,2002:seq")
                throw new InvalidDataException();
            if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw new InvalidDataException();
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1) throw new InvalidDataException();
        return yaml.Documents[0].RootNode;
    }

    private static IEnumerable<YamlMappingNode> Sequence(YamlNode node)
    {
        if (node is not YamlSequenceNode sequence) throw new InvalidDataException();
        foreach (var child in sequence.Children)
        {
            if (child is not YamlMappingNode map) throw new InvalidDataException();
            yield return map;
        }
    }

    private static void Apply(YamlMappingNode patch, List<YamlMappingNode> rows, Dictionary<string, YamlMappingNode> ids)
    {
        if (Get(patch, "id") is { } idNode && Text(idNode) is null
            || Get(patch, "name") is { } nameNode && Text(nameNode) is null) throw new InvalidDataException();
        var id = Text(Get(patch, "id"));
        if (Get(patch, "insert") is { } insert)
        {
            var additions = Sequence(insert).ToArray();
            if (id is { Length: > 0 })
            {
                if (!ids.TryGetValue(id, out var parent) || !IsTrue(Get(parent, "group"))) return;
                if (Get(parent, "config") is not YamlSequenceNode children)
                {
                    children = new YamlSequenceNode();
                    parent.Children[new YamlScalarNode("config")] = children;
                }
                foreach (var row in additions) children.Add(row);
            }
            else rows.AddRange(additions);
            Index(additions, ids);
            return;
        }
        if (id is not { Length: > 0 } || !ids.TryGetValue(id, out var target)) return;
        var name = Text(Get(patch, "name"));
        if (name is { Length: > 0 } && name != Text(Get(target, "name"))) return;
        foreach (var (key, value) in patch.Children)
            if (Text(key) is not ("id" or "name")) target.Children[key] = value;
    }

    private static void Index(IEnumerable<YamlMappingNode> rows, Dictionary<string, YamlMappingNode> ids)
    {
        foreach (var row in rows)
        {
            if (Text(Get(row, "id")) is { Length: > 0 } id && !ids.TryAdd(id, row)) throw new InvalidDataException();
            if (IsTrue(Get(row, "group")) && Get(row, "config") is YamlSequenceNode children) Index(Sequence(children), ids);
        }
    }

    private static IEnumerable<(YamlMappingNode Row, bool Disabled)> Flatten(IEnumerable<YamlMappingNode> rows, bool parentDisabled)
    {
        foreach (var row in rows)
        {
            var disabled = parentDisabled || Get(row, "disabled") is { } value && !IsFalse(value);
            yield return (row, disabled);
            var group = Get(row, "group");
            var runtimeGroup = Text(Get(row, "name")) is "cordis:group" or "@deepseek-ai/cordis-plugin-group";
            if (group is not null || runtimeGroup)
            {
                if (group is not null && !IsTrue(group) || Get(row, "config") is not YamlSequenceNode children) throw new InvalidDataException();
                var unknownScope = !runtimeGroup || row.Children.Keys.Any(k => Text(k) is not ("id" or "name" or "config" or "disabled" or "group"));
                foreach (var child in Flatten(Sequence(children), disabled || unknownScope)) yield return child;
            }
        }
    }

    private static YamlNode? Get(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static bool Plain(YamlScalarNode node) => node.Style == ScalarStyle.Plain
        && (node.Tag.IsEmpty || node.Tag.ToString() == "tag:yaml.org,2002:bool");
    private static bool IsTrue(YamlNode? node) => node is YamlScalarNode scalar && Plain(scalar) && scalar.Value == "true";
    private static bool IsFalse(YamlNode? node) => node is YamlScalarNode scalar && Plain(scalar) && scalar.Value == "false";
    private static string? Text(YamlNode? node) => node is YamlScalarNode scalar
        && (scalar.Tag.IsEmpty || scalar.Tag.ToString() == "tag:yaml.org,2002:str") ? scalar.Value : null;
}
