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
            "Penghou.Hetu.CodeGraphFactKind",
            "Penghou.Hetu.CodeGraphFactProvenance",
            "Penghou.Hetu.CodeGraphPluginRegistry",
            "Penghou.Hetu.CodeGraphPluginSelectionException",
            "Penghou.Hetu.CodeGraphQueryOptions",
            "Penghou.Hetu.CodeGraphQueryDescriptor",
            "Penghou.Hetu.CodeGraphQueryEnvelope`1",
            "Penghou.Hetu.CodeGraphQueryService",
            "Penghou.Hetu.CodeGraphPublication",
            "Penghou.Hetu.CodeGraphPublicationChangedException",
            "Penghou.Hetu.CodeGraphPublicationQuery",
            "Penghou.Hetu.CodeGraphMultiTraversalResult",
            "Penghou.Hetu.CodeGraphStoreHealth",
            "Penghou.Hetu.CodeGraphStoreHealthStatus",
            "Penghou.Hetu.CodeGraphTraversalQuery",
            "Penghou.Hetu.CodeGraphTraversalResult",
            "Penghou.Hetu.CodeGraphTruncationReason",
            "Penghou.Hetu.CodeIndexRunManifest",
            "Penghou.Hetu.CodeIndexRunStatus",
            "Penghou.Hetu.CodeIndexPlan",
            "Penghou.Hetu.CodeIndexPlanItem",
            "Penghou.Hetu.CodeIndexPlanner",
            "Penghou.Hetu.CodeIndexPlanningOptions",
            "Penghou.Hetu.CodeIndexPlanStatus",
            "Penghou.Hetu.CodeIndexingDiagnostics",
            "Penghou.Hetu.CodeIndexingOptions",
            "Penghou.Hetu.CodeIndexingResult",
            "Penghou.Hetu.CodeIndexingService",
            "Penghou.Hetu.CodeIndexIdentity",
            "Penghou.Hetu.CodePluginIndexingDiagnostics",
            "Penghou.Hetu.CodePluginIndexingStatus",
            "Penghou.Hetu.CodeIndexUnitReplacement",
            "Penghou.Hetu.CodeRepositoryManifest",
            "Penghou.Hetu.CodeRepositoryDescriptor",
            "Penghou.Hetu.CodeRepositoryDiscoveryEventKind",
            "Penghou.Hetu.CodeRepositoryEntry",
            "Penghou.Hetu.CodeRepositoryEnumerationLimitException",
            "Penghou.Hetu.CodeRepositoryEnumerationOptions",
            "Penghou.Hetu.CodeRepositoryProviderNotFoundException",
            "Penghou.Hetu.CodeRepositoryProviderRegistry",
            "Penghou.Hetu.CodeRepositoryProviderSelectionException",
            "Penghou.Hetu.CodeRepositoryIndexState",
            "Penghou.Hetu.CodeSourceManifest",
            "Penghou.Hetu.CodeSourceChangedDuringIndexingException",
            "Penghou.Hetu.CodeSourceSizeLimitException",
            "Penghou.Hetu.CodeSymbolLookupResult",
            "Penghou.Hetu.FileSystemCodeRepositoryProvider",
            "Penghou.Hetu.HetuBuilder",
            "Penghou.Hetu.HetuHost",
            "Penghou.Hetu.HetuHostBuilder",
            "Penghou.Hetu.HetuHostHealth",
            "Penghou.Hetu.ICodeGraphIndexStore",
            "Penghou.Hetu.ICodeGraphReader",
            "Penghou.Hetu.ICodeGraphStore",
            "Penghou.Hetu.ICodeGraphStoreHealthCheck",
            "Penghou.Hetu.ICodeRepositoryProvider",
            "Penghou.Hetu.ICodeRepositorySource",
            "Penghou.Hetu.InMemoryCodeGraphStore"
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }
}
