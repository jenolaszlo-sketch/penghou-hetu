using Penghou.Hetu;

namespace Penghou.Hetu.Tests;

public sealed class GraphSemanticsTests
{
    [Fact]
    public void PartialSymbol_HasStableIdentityAndMultiplePhysicalDeclarations()
    {
        var symbolId = new CodeSymbolId("csharp:type:Example.PartialWidget");
        var symbolNodeId = new CodeNodeId("node:symbol:partial-widget");
        var declarations = new[]
        {
            new CodeGraphDeclaration(
                new CodeDeclarationId("declaration:partial-widget:one"),
                symbolId,
                symbolNodeId,
                new CodeLocation("src/Widget.Core.cs", 3, 1, 8, 2)),
            new CodeGraphDeclaration(
                new CodeDeclarationId("declaration:partial-widget:two"),
                symbolId,
                symbolNodeId,
                new CodeLocation("src/Widget.IO.cs", 5, 1, 12, 2))
        };

        Assert.Equal(2, declarations.Length);
        Assert.All(
            declarations,
            declaration => Assert.Equal(symbolId, declaration.SymbolId));
        Assert.Equal(
            2,
            declarations.Select(declaration => declaration.Location.Path)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void CrossFileRelationship_CarriesItsOwnEvidenceLocation()
    {
        var edge = new CodeGraphEdge(
            new CodeEdgeId("edge:caller:callee"),
            new CodeNodeId("node:method:caller"),
            new CodeNodeId("node:method:callee"),
            CodeEdgeKinds.Calls,
            new CodeEvidence(
                CodeEvidenceKind.Semantic,
                "roslyn",
                location: new CodeLocation(
                    "src/Caller.cs",
                    12,
                    9,
                    12,
                    22)));

        Assert.Equal("src/Caller.cs", edge.Evidence.Location!.Path);
        Assert.NotEqual(edge.SourceId, edge.TargetId);
    }
}
