using DeepSeekHarnessCleaner.Infrastructure;
using Xunit;

namespace DeepSeekHarnessCleaner.Tests;

public sealed class WorkspaceStoragePolicyTests
{
    internal const string Default = """
        - insert:
          - id: storage
            name: '@deepseek-ai/dsh-storage'
          - id: storage-json
            name: '@deepseek-ai/dsh-storage-json'
            config:
              root: !!js dshHomePath('storages')
          - id: storage-domain
            name: '@deepseek-ai/dsh-storage-domain'
            config:
              backend: json
          - id: workspace
            name: '@deepseek-ai/dsh-workspace'
        """;

    [Fact]
    public void AcceptsReviewedCore_WithoutRunningAnyExpressions()
    {
        Assert.True(WorkspaceStoragePolicy.Allows([Default]));
        Assert.True(WorkspaceStoragePolicy.Allows([Default, """
            - insert:
              - id: unrelated
                name: arbitrary-plugin
                config:
                  secret: must-not-be-logged
                  expression: !!js throw new Error('must never execute')
            """]));
    }

    [Theory]
    [InlineData("- id: storage-domain\n  config: {backend: sqlite}")]
    [InlineData("- id: storage-domain\n  config: {backend: json, routes: {workspace: other}}")]
    [InlineData("- id: storage-domain\n  config: {routes: {workspace: json}}")]
    [InlineData("- id: storage-json\n  config: {root: other}")]
    [InlineData("- id: storage-json\n  config: {root: {__jsExpr: \"dshHomePath('storages')\"}}")]
    [InlineData("- id: storage-json\n  config: {root: !!js \"process.env.SOMEWHERE\"}")]
    [InlineData("- id: workspace\n  disabled: true")]
    [InlineData("- id: workspace\n  disabled: 'false'")]
    [InlineData("- id: workspace\n  disabled: !!js false")]
    [InlineData("- id: workspace\n  config: {unknown: true}")]
    [InlineData("- id: workspace\n  isolate: {storageDomain: true}")]
    [InlineData("- id: storage-domain\n  inject: {storage: false}")]
    [InlineData("- id: storage-domain\n  config: {backend: !!js \"'json'\"}")]
    [InlineData("- id: !!js workspace\n  disabled: false")]
    public void RejectsUnknownRoutingRootsAndDynamicCoreConditions(string patch)
    {
        Assert.False(WorkspaceStoragePolicy.Allows([Default, patch]));
    }

    [Fact]
    public void ReplacesConfigRatherThanMerging_AndRespectsPatchNameMismatch()
    {
        Assert.False(WorkspaceStoragePolicy.Allows([Default, "- id: storage-domain\n  config: {routes: {other: json}}"]));
        Assert.True(WorkspaceStoragePolicy.Allows([Default, "- id: storage-domain\n  name: wrong-plugin\n  config: {backend: sqlite}"]));
        Assert.True(WorkspaceStoragePolicy.Allows([Default, "- id: storage-domain\n  config: {backend: json, routes: {other: sqlite}}"]));
    }

    [Fact]
    public void AppliesAllLayersInOrder_AndAllowsUnrelatedPreferenceChanges()
    {
        Assert.True(WorkspaceStoragePolicy.Allows([Default,
            "- id: storage-domain\n  config: {backend: other}",
            "- id: storage-domain\n  config: {backend: json, routes: {workspace: json}}"]));
        Assert.True(WorkspaceStoragePolicy.Allows([Default, "- id: missing\n  config: {model: changed}"]));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("!!js false")]
    [InlineData("'false'")]
    public void DoesNotIgnoreParentDisableState(string disabled)
    {
        var nested = "- insert:\n  - id: group\n    name: cordis:group\n    group: true\n    disabled: " + disabled + "\n    config: []\n";
        var core = Default.Replace("- insert:", "- id: group\n  insert:", StringComparison.Ordinal);
        Assert.False(WorkspaceStoragePolicy.Allows([nested, core]));
        Assert.True(WorkspaceStoragePolicy.Allows([nested, core, "- id: group\n  disabled: false"]));
    }

    [Theory]
    [InlineData("- insert: &a [*a]")]
    [InlineData("- insert: !unknown []")]
    [InlineData("- id: storage-domain\n  config: !!js {backend: json}")]
    [InlineData("- id: workspace\n  disabled: false\n  disabled: true")]
    [InlineData("[]\n---\n[]")]
    [InlineData("[broken")]
    [InlineData("- insert: [{id: extra, name: '@deepseek-ai/dsh-workspace'}]")]
    [InlineData("- insert: [{id: workspace, name: unused}]")]
    [InlineData("- insert: [{id: include, name: 'cordis:include', config: {path: unknown.yml}}]")]
    public void RejectsAmbiguousOrMalformedLayers(string patch)
    {
        Assert.False(WorkspaceStoragePolicy.Allows([Default, patch]));
    }

    [Fact]
    public void LimitsInputSizeAndDepth()
    {
        Assert.False(WorkspaceStoragePolicy.Allows([new string(' ', 1024 * 1024 + 1)]));
        Assert.False(WorkspaceStoragePolicy.Allows([new string('[', 40) + new string(']', 40)]));
        Assert.False(WorkspaceStoragePolicy.Allows([]));
    }
}
