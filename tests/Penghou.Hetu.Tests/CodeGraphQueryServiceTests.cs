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

    [Fact]
    public async Task ResolveSymbols_ReturnsCandidatesPerName()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:batch");
        var runId = new CodeIndexRunId("run:batch");
        var pluginId = new CodePluginId("plugin:batch");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var alpha = Node("alpha", "Example.Alpha");
        var beta = Node("beta", "Example.Beta");
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:batch")),
            [alpha, beta]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);
        var queries = new CodeGraphQueryService(store);

        var results = await queries.ResolveSymbolsAsync(
            repositoryId,
            ["Example.Alpha", "Example.Beta", "Example.Missing"]);

        Assert.True(results.ContainsKey("Example.Alpha"));
        Assert.Equal(alpha.Id, results["Example.Alpha"].Candidates.Single().Id);
        Assert.Equal(beta.Id, results["Example.Beta"].Candidates.Single().Id);
        Assert.Empty(results["Example.Missing"].Candidates);
    }

    [Fact]
    public async Task GetDeclarationsInFile_ReturnsOrderedDeclarationsForOneFile()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:file-decls");
        var runId = new CodeIndexRunId("run:file-decls");
        var pluginId = new CodePluginId("plugin:file-decls");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));

        var fileNode = new CodeGraphNode(
            new CodeNodeId("node:file-a"),
            CodeNodeKinds.File,
            "A.cs",
            "src/A.cs");
        var symbolA = new CodeGraphNode(
            new CodeNodeId("node:sym-a"),
            CodeNodeKinds.Callable,
            "DoWork",
            "Example.DoWork()",
            new CodeSymbolId("symbol:sym-a"));
        var declarationA = new CodeGraphDeclaration(
            new CodeDeclarationId("decl:a"),
            symbolA.SymbolId!,
            symbolA.Id,
            new CodeLocation("src/A.cs", 5, 1, 5, 20));

        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:f")),
            [fileNode, symbolA],
            [declarationA],
            [new CodeGraphEdge(
                new CodeEdgeId("edge:decl-a"),
                fileNode.Id,
                symbolA.Id,
                CodeEdgeKinds.Declares,
                new CodeEvidence(CodeEvidenceKind.Semantic, "test", location: declarationA.Location))]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);
        var queries = new CodeGraphQueryService(store);

        var declarations = await queries.GetDeclarationsInFileAsync(
            repositoryId,
            "src/A.cs");

        Assert.Single(declarations);
        Assert.Equal("src/A.cs", declarations[0].Location.Path);
    }

    [Fact]
    public async Task PublicSurface_CrossesFileDeclarationBoundaryWithProvenance()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:surface");
        var runId = new CodeIndexRunId("run:surface");
        var pluginId = new CodePluginId("plugin:surface");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var project = new CodeGraphNode(
            new("node:project"), CodeNodeKinds.Project, "Sample", "src/Sample.csproj");
        var file = new CodeGraphNode(
            new("node:file"), CodeNodeKinds.File, "Service.cs", "src/Service.cs");
        var visible = SurfaceNode("visible", "Sample.VisibleService", "public");
        var hidden = SurfaceNode("hidden", "Sample.HiddenService", "internal");
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:surface")),
            [project, file, visible, hidden],
            edges:
            [
                Relationship("contains", project.Id, file.Id, CodeEdgeKinds.Contains),
                Relationship("visible", file.Id, visible.Id, CodeEdgeKinds.Declares),
                Relationship("hidden", file.Id, hidden.Id, CodeEdgeKinds.Declares)
            ]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);
        var queries = new CodeGraphQueryService(store);

        var surface = await queries.GetPublicSurfaceAsync(
            repositoryId,
            "src/Sample.csproj");
        var attributed = await queries.GetPublicSurfaceWithProvenanceAsync(
            repositoryId,
            "src/Sample.csproj");

        Assert.Equal([visible.Id], surface.Select(node => node.Id));
        Assert.NotNull(attributed);
        Assert.Equal([visible.Id], attributed.Result.Select(node => node.Id));
        var provenance = Assert.Single(attributed.Provenance);
        Assert.Equal(visible.Id.Value, provenance.FactId);
        Assert.Equal(runId, attributed.Publication.IndexRunId);
    }

    [Fact]
    public async Task PublicationQuery_RejectsLaterPublication()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:bound");
        var pluginId = new CodePluginId("plugin:bound");
        var firstRun = new CodeIndexRunId("run:first");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, firstRun, started, plugins: [pluginId]));
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", firstRun, new("unit:bound")),
            [Node("bound", "Example.Bound")]));
        await CompleteAsync(store, repositoryId, firstRun, pluginId, started);
        var queries = new CodeGraphQueryService(store);
        var bound = await queries.OpenLatestPublicationAsync(repositoryId);
        Assert.NotNull(bound);
        var impact = await bound.GetImpactSetAsync(new("node:bound"));
        Assert.Equal(firstRun, impact.Publication.IndexRunId);
        Assert.Equal([new CodeNodeId("node:bound")],
            impact.Result.Nodes.Select(node => node.Id));

        var secondRun = new CodeIndexRunId("run:second");
        await store.StoreIndexRunAsync(new(
            repositoryId,
            secondRun,
            started.AddMinutes(1),
            plugins: [pluginId]));
        await CompleteAsync(
            store,
            repositoryId,
            secondRun,
            pluginId,
            started.AddMinutes(1));

        var exception = await Assert.ThrowsAsync<
            CodeGraphPublicationChangedException>(async () =>
            await bound!.FindSymbolAsync("Example.Bound"));

        Assert.Equal(firstRun, exception.Expected.IndexRunId);
        Assert.Equal(secondRun, exception.Actual!.IndexRunId);
    }

    [Fact]
    public async Task BatchQueries_AreBoundedAndShareOnePublication()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:multi");
        var runId = new CodeIndexRunId("run:multi");
        var pluginId = new CodePluginId("plugin:multi");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var target = Node("target-multi", "Example.Target");
        var first = Node("first-multi", "Example.First");
        var second = Node("second-multi", "Example.Second");
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:multi")),
            [target, first, second],
            edges:
            [
                Relationship("call-first", first.Id, target.Id, CodeEdgeKinds.Calls),
                Relationship("call-second", second.Id, target.Id, CodeEdgeKinds.Calls)
            ]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);
        var queries = new CodeGraphQueryService(store);

        var symbols = await queries.ResolveSymbolsWithProvenanceAsync(
            repositoryId,
            ["Example.Second", "Example.First", "Example.First"]);
        var impacts = await queries.GetImpactSetsWithProvenanceAsync(
            repositoryId,
            [target.Id, first.Id],
            new CodeGraphQueryOptions(maxDepth: 1));

        Assert.NotNull(symbols);
        Assert.Equal(
            ["Example.First", "Example.Second"],
            symbols.Result.Keys);
        Assert.Equal(runId, symbols.Publication.IndexRunId);
        Assert.NotNull(impacts);
        Assert.Equal([first.Id, second.Id, target.Id],
            impacts.Result.Results[target.Id.Value].Nodes
                .Select(node => node.Id)
                .OrderBy(id => id.Value, StringComparer.Ordinal));
        Assert.Equal(2, impacts.Result.Results.Count);
    }

    [Fact]
    public async Task EmptyBatch_StillIdentifiesPublication()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:empty-batch");
        var runId = new CodeIndexRunId("run:empty-batch");
        var pluginId = new CodePluginId("plugin:empty-batch");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);

        var result = await new CodeGraphQueryService(store)
            .ResolveSymbolsWithProvenanceAsync(repositoryId, []);

        Assert.NotNull(result);
        Assert.Equal(runId, result.Publication.IndexRunId);
        Assert.Empty(result.Result);
    }

    [Fact]
    public async Task BatchQueries_RejectRequestsBeyondTheirDeclaredLimits()
    {
        var store = new InMemoryCodeGraphStore();
        var queries = new CodeGraphQueryService(store);
        var repositoryId = new CodeRepositoryId("repo:limits");

        var symbolException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await queries.ResolveSymbolsAsync(
                repositoryId,
                Enumerable.Range(0, 101).Select(index => $"Example.Symbol{index}").ToArray()));
        var impactException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await queries.GetImpactSetsAsync(
                repositoryId,
                Enumerable.Range(0, 33).Select(index => new CodeNodeId($"node:{index}")).ToArray()));

        Assert.Equal("qualifiedNames", symbolException.ParamName);
        Assert.Equal("seedNodeIds", impactException.ParamName);
    }

    [Fact]
    public async Task DeclarationsInFileWithProvenance_ReturnsOnlySelectedDeclarations()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:file-provenance");
        var runId = new CodeIndexRunId("run:file-provenance");
        var pluginId = new CodePluginId("plugin:file-provenance");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var file = new CodeGraphNode(
            new("node:file-provenance"), CodeNodeKinds.File, "A.cs", "src/A.cs");
        var symbol = Node("file-symbol", "Example.FileSymbol");
        var selected = new CodeGraphDeclaration(
            new("decl:selected"), symbol.SymbolId!, symbol.Id,
            new("src/A.cs", 1, 1, 1, 10));
        var other = new CodeGraphDeclaration(
            new("decl:other"), symbol.SymbolId!, symbol.Id,
            new("src/B.cs", 1, 1, 1, 10));
        await store.StageIndexUnitAsync(new(
            new(repositoryId, pluginId, "1.0.0", runId, new("unit:file-provenance")),
            [file, symbol],
            [selected, other],
            [Relationship("file-declares", file.Id, symbol.Id, CodeEdgeKinds.Declares)]));
        await CompleteAsync(store, repositoryId, runId, pluginId, started);

        var envelope = await new CodeGraphQueryService(store)
            .GetDeclarationsInFileWithProvenanceAsync(repositoryId, "src/A.cs");

        Assert.NotNull(envelope);
        Assert.Equal([selected.Id], envelope.Result.Select(item => item.Id));
        var provenance = Assert.Single(envelope.Provenance);
        Assert.Equal(selected.Id.Value, provenance.FactId);
    }

    private static CodeGraphNode Node(string id, string name) => new(
        new CodeNodeId($"node:{id}"),
        CodeNodeKinds.Callable,
        name[(name.LastIndexOf('.') + 1)..],
        name,
            new CodeSymbolId($"symbol:{id}"));

    private static CodeGraphNode SurfaceNode(
        string id,
        string name,
        string access) =>
        new(
            new CodeNodeId($"node:{id}"),
            CodeNodeKinds.Type,
            name[(name.LastIndexOf('.') + 1)..],
            name,
            new CodeSymbolId($"symbol:{id}"),
            new Dictionary<string, CodePropertyValue>
            {
                ["access"] = new CodeTextProperty(access)
            });

    private static CodeGraphEdge Relationship(
        string id,
        CodeNodeId source,
        CodeNodeId target,
        CodeEdgeKind kind) =>
        new(
            new CodeEdgeId($"edge:{id}"),
            source,
            target,
            kind,
            new CodeEvidence(CodeEvidenceKind.Semantic, "tests"));

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
