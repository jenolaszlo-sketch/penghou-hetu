using Penghou.Hetu;
using System.Reflection;

namespace Penghou.Hetu.Tests;

public sealed class PublicApiContractTests
{
    [Fact]
    public void Abstractions_PublicTypeSnapshot_IsIntentional()
    {
        var actual = typeof(CodeGraphNode).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "Penghou.Hetu.CodeBooleanProperty",
            "Penghou.Hetu.CodeDeclarationId",
            "Penghou.Hetu.CodeEdgeId",
            "Penghou.Hetu.CodeEdgeKind",
            "Penghou.Hetu.CodeEdgeKinds",
            "Penghou.Hetu.CodeEvidence",
            "Penghou.Hetu.CodeEvidenceKind",
            "Penghou.Hetu.CodeFactOrigin",
            "Penghou.Hetu.CodeGraphBatch",
            "Penghou.Hetu.CodeGraphBatchLimits",
            "Penghou.Hetu.CodeGraphBatchRejectedException",
            "Penghou.Hetu.CodeGraphCapabilities",
            "Penghou.Hetu.CodeGraphDeclaration",
            "Penghou.Hetu.CodeGraphEdge",
            "Penghou.Hetu.CodeGraphExtractionResult",
            "Penghou.Hetu.CodeGraphNode",
            "Penghou.Hetu.CodeGraphPluginContext",
            "Penghou.Hetu.CodeGraphSource",
            "Penghou.Hetu.CodeGraphSourceChange",
            "Penghou.Hetu.CodeGraphSourceChangeKind",
            "Penghou.Hetu.CodeGraphValidationError",
            "Penghou.Hetu.CodeGraphValidationErrorKind",
            "Penghou.Hetu.CodeIndexRunId",
            "Penghou.Hetu.CodeIndexUnitId",
            "Penghou.Hetu.CodeIntegerProperty",
            "Penghou.Hetu.CodeLocation",
            "Penghou.Hetu.CodeNodeId",
            "Penghou.Hetu.CodeNodeKind",
            "Penghou.Hetu.CodeNodeKinds",
            "Penghou.Hetu.CodeNumberProperty",
            "Penghou.Hetu.CodePluginId",
            "Penghou.Hetu.CodePropertyValue",
            "Penghou.Hetu.CodeRepositoryId",
            "Penghou.Hetu.CodeSymbolId",
            "Penghou.Hetu.CodeTextListProperty",
            "Penghou.Hetu.CodeTextProperty",
            "Penghou.Hetu.ICodeGraphExtractionSession",
            "Penghou.Hetu.ICodeGraphPlugin",
            "Penghou.Hetu.ICodeGraphSink"
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Abstractions_DoNotReferenceParserDatabaseOrAiFrameworks()
    {
        var references = typeof(CodeGraphNode).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();
        var forbiddenPrefixes = new[]
        {
            "Microsoft.CodeAnalysis",
            "Antlr",
            "Ladybug",
            "Kuzu",
            "Microsoft.Extensions.AI"
        };

        Assert.DoesNotContain(
            references,
            reference => forbiddenPrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PluginContract_DoesNotExposeParserSpecificTypes()
    {
        var members = typeof(ICodeGraphPlugin)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.ToString() ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            members,
            member => member.Contains("Roslyn", StringComparison.OrdinalIgnoreCase) ||
                member.Contains("SyntaxTree", StringComparison.OrdinalIgnoreCase));
    }
}
