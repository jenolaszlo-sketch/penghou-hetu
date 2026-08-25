using Penghou.Hetu;
using System.Text.Json;

namespace Penghou.Hetu.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void GraphBatch_RoundTripsWithoutLosingTypedPropertiesOrEvidence()
    {
        var symbolId = new CodeSymbolId("csharp:type:Example.Widget");
        var symbolNodeId = new CodeNodeId("node:symbol:widget");
        var origin = new CodeFactOrigin(
            new CodeRepositoryId("repo:sample"),
            new CodePluginId("hetu-csharp"),
            "0.1.0",
            new CodeIndexRunId("run:one"),
            new CodeIndexUnitId("project:sample"));
        var batch = new CodeGraphBatch(
            origin,
            nodes:
            [
                new CodeGraphNode(
                    symbolNodeId,
                    CodeNodeKinds.Type,
                    "Widget",
                    "Example.Widget",
                    symbolId,
                    new Dictionary<string, CodePropertyValue>
                    {
                        ["abstract"] = new CodeBooleanProperty(false),
                        ["arity"] = new CodeIntegerProperty(1),
                        ["language"] = new CodeTextProperty("csharp"),
                        ["modifiers"] = new CodeTextListProperty(
                            ["public", "partial"]),
                        ["weight"] = new CodeNumberProperty(1.25)
                    })
            ],
            declarations:
            [
                new CodeGraphDeclaration(
                    new CodeDeclarationId("declaration:widget:one"),
                    symbolId,
                    symbolNodeId,
                    new CodeLocation("src/Widget.cs", 2, 1, 5, 2))
            ],
            edges:
            [
                new CodeGraphEdge(
                    new CodeEdgeId("edge:declares:widget"),
                    new CodeNodeId("node:file:widget"),
                    symbolNodeId,
                    CodeEdgeKinds.Declares,
                    new CodeEvidence(
                        CodeEvidenceKind.Semantic,
                        "roslyn",
                        "5.0",
                        new CodeLocation("src/Widget.cs", 2, 1, 5, 2)))
            ],
            completesIndexUnit: true);

        var json = JsonSerializer.Serialize(batch);
        var restored = JsonSerializer.Deserialize<CodeGraphBatch>(json);

        Assert.True(
            json.IndexOf("abstract", StringComparison.Ordinal) <
            json.IndexOf("arity", StringComparison.Ordinal));
        Assert.True(
            json.IndexOf("arity", StringComparison.Ordinal) <
            json.IndexOf("language", StringComparison.Ordinal));
        Assert.NotNull(restored);
        Assert.Equal(batch.Origin, restored.Origin);
        Assert.Equal(batch.Nodes.Single().Id, restored.Nodes.Single().Id);
        Assert.IsType<CodeBooleanProperty>(
            restored.Nodes.Single().Properties["abstract"]);
        Assert.IsType<CodeIntegerProperty>(
            restored.Nodes.Single().Properties["arity"]);
        Assert.IsType<CodeTextProperty>(
            restored.Nodes.Single().Properties["language"]);
        Assert.IsType<CodeTextListProperty>(
            restored.Nodes.Single().Properties["modifiers"]);
        Assert.IsType<CodeNumberProperty>(
            restored.Nodes.Single().Properties["weight"]);
        Assert.Equal(
            CodeEvidenceKind.Semantic,
            restored.Edges.Single().Evidence.Kind);
        Assert.True(restored.CompletesIndexUnit);
    }

    [Fact]
    public void RepositoryIndexState_RoundTripsAsPortableIncrementalData()
    {
        var state = new CodeRepositoryIndexState(
            new CodeRepositoryId("repo:sample"),
            new CodeIndexRunId("run:one"),
            [
                new CodeSourceManifest(
                    new CodePluginId("plugin:csharp"),
                    "1.2.3",
                    "src/Example.cs",
                    "sha256:example")
            ],
            "commit:abc123",
            true);

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<CodeRepositoryIndexState>(json);

        Assert.NotNull(restored);
        Assert.Equal(state.RepositoryId, restored.RepositoryId);
        Assert.Equal(state.IndexRunId, restored.IndexRunId);
        Assert.Equal(state.SnapshotIdentity, restored.SnapshotIdentity);
        Assert.True(restored.IsConsistentSnapshot);
        Assert.Equal(state.Sources.Single(), restored.Sources.Single());
    }

    [Fact]
    public void ExtractionResult_RoundTripsDiagnosticsAndObsoleteUnits()
    {
        var result = new CodeGraphExtractionResult(
            [new CodeIndexUnitId("unit:obsolete")],
            sourcesExamined: 4,
            sourcesContributingFacts: 3,
            unresolvedRelationships: 2,
            warningCodes: ["csharp.unresolved", "csharp.project-warning"]);

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<CodeGraphExtractionResult>(json);

        Assert.NotNull(restored);
        Assert.Equal(result.ObsoleteIndexUnits.Single(), restored.ObsoleteIndexUnits.Single());
        Assert.Equal(4, restored.SourcesExamined);
        Assert.Equal(3, restored.SourcesContributingFacts);
        Assert.Equal(2, restored.UnresolvedRelationships);
        Assert.Equal(result.WarningCodes, restored.WarningCodes);
    }
}
