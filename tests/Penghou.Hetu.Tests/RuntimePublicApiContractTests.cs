namespace Penghou.Hetu.Tests;

public sealed class RuntimePublicApiContractTests
{
    [Fact]
    public void Runtime_PublicTypeSnapshot_IsIntentional()
    {
        var actual = typeof(ICodeGraphStore).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "Penghou.Hetu.CodeGraphBatchValidator",
            "Penghou.Hetu.CodeGraphDirection",
            "Penghou.Hetu.CodeGraphIngestionDiagnostics",
            "Penghou.Hetu.CodeGraphIngestionSink",
            "Penghou.Hetu.CodeGraphPluginRegistry",
            "Penghou.Hetu.CodeGraphPluginSelectionException",
            "Penghou.Hetu.CodeGraphTraversalQuery",
            "Penghou.Hetu.CodeGraphTraversalResult",
            "Penghou.Hetu.CodeIndexRunManifest",
            "Penghou.Hetu.CodeIndexRunStatus",
            "Penghou.Hetu.CodeIndexUnitReplacement",
            "Penghou.Hetu.CodeRepositoryManifest",
            "Penghou.Hetu.HetuBuilder",
            "Penghou.Hetu.ICodeGraphStore",
            "Penghou.Hetu.InMemoryCodeGraphStore"
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }
}
