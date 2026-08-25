namespace Penghou.Hetu.Tests;

public sealed class CodeGraphQueryServiceTests
{
    [Fact]
    public async Task SemanticQueries_UseExactDirectionKindsEvidenceAndBounds()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:queries");
        var runId = new CodeIndexRunId("run:queries");
        var pluginId = new CodePluginId("plugin:queries");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));

        var target = Node("target", "Example.Target");
        var semanticCaller = Node("semantic-caller", "Example.SemanticCaller");
        var heuristicCaller = Node("heuristic-caller", "Example.HeuristicCaller");
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:queries")),
            [target, semanticCaller, heuristicCaller],
            edges:
            [
                Edge("semantic", semanticCaller.Id, target.Id, CodeEvidenceKind.Semantic),
                Edge("heuristic", heuristicCaller.Id, target.Id, CodeEvidenceKind.Heuristic)
            ]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);
        var queries = new CodeGraphQueryService(store);

        var callers = await queries.FindCallersAsync(
            repositoryId,
            target.Id,
            new CodeGraphQueryOptions(evidenceKinds: [CodeEvidenceKind.Semantic]));

        Assert.Equal([target.Id, semanticCaller.Id], callers.Nodes.Select(node => node.Id));
        Assert.Single(callers.Edges);
        Assert.Equal(CodeEvidenceKind.Semantic, callers.Edges[0].Evidence.Kind);
    }

    [Fact]
    public async Task ExactLookup_PreservesAmbiguityAndDeclarationOrder()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:lookup");
        var runId = new CodeIndexRunId("run:lookup");
        var pluginId = new CodePluginId("plugin:lookup");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var first = Node("z", "Example.Duplicate");
        var second = Node("a", "Example.Duplicate");
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:lookup")),
            [first, second]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);

        var result = await new CodeGraphQueryService(store)
            .FindSymbolAsync(repositoryId, "Example.Duplicate");

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.SingleOrDefault);
        Assert.Equal([second.Id, first.Id], result.Candidates.Select(node => node.Id));
    }

    private static CodeGraphNode Node(string id, string name) => new(
        new CodeNodeId($"node:{id}"),
        CodeNodeKinds.Callable,
        name[(name.LastIndexOf('.') + 1)..],
        name,
            new CodeSymbolId($"symbol:{id}"));

    private static ValueTask CompleteAsync(
        ICodeGraphStore store,
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CodePluginId pluginId,
        DateTimeOffset started) =>
        store.CompleteIndexRunAsync(
            new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, runId, []));

    private static CodeGraphEdge Edge(
        string id,
        CodeNodeId source,
        CodeNodeId target,
        CodeEvidenceKind evidenceKind) => new(
            new CodeEdgeId($"edge:{id}"),
            source,
            target,
            CodeEdgeKinds.Calls,
            new CodeEvidence(evidenceKind, "tests", confidence:
                evidenceKind == CodeEvidenceKind.Heuristic ? 0.9 : null));
}
