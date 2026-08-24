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
}
