namespace Penghou.Hetu.Testing;

/// <summary>Verifies the durable semantic laws required of every graph store.</summary>
public static class CodeGraphStoreConformanceSuite
{
    public static async Task<CodeGraphStoreConformanceReport> VerifyAsync(
        ICodeGraphStoreFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var store = fixture.CreateStore() ??
            throw new CodeGraphStoreConformanceException(
                "fixture returned a null store");
        var checks = new List<string>();
        var repositoryId = new CodeRepositoryId($"repo:{Guid.NewGuid():N}");
        var runId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
        var startedAt = DateTimeOffset.UtcNow;

        var repository = new CodeRepositoryManifest(
            repositoryId,
            "conformance repository",
            "repo://conformance",
            startedAt);
        await store.UpsertRepositoryAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        var restoredRepository = await store.GetRepositoryAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        Require(restoredRepository == repository, "repository manifest round-trip");
        checks.Add("repository-manifest-round-trip");

        var pluginId = new CodePluginId("plugin:conformance");
        var running = new CodeIndexRunManifest(
            repositoryId,
            runId,
            startedAt,
            plugins: [pluginId]);
        await store.StoreIndexRunAsync(running, cancellationToken)
            .ConfigureAwait(false);
        await store.StoreIndexRunAsync(running, cancellationToken)
            .ConfigureAwait(false);
        checks.Add("index-run-idempotent-start");

        var shared = Node("shared", "Example.Shared");
        var firstOnly = Node("first", "Example.First");
        var secondOnly = Node("second", "Example.Second");
        var firstDeclaration = Declaration(
            "first",
            shared,
            "src/Shared.One.cs");
        var secondDeclaration = Declaration(
            "second",
            shared,
            "src/Shared.Two.cs");
        var first = Replacement(
            repositoryId,
            runId,
            pluginId,
            "unit:first",
            [shared, firstOnly],
            [firstDeclaration]);
        var second = Replacement(
            repositoryId,
            runId,
            pluginId,
            "unit:second",
            [shared, secondOnly],
            [secondDeclaration]);

        await store.ReplaceIndexUnitAsync(first, cancellationToken)
            .ConfigureAwait(false);
        await store.ReplaceIndexUnitAsync(first, cancellationToken)
            .ConfigureAwait(false);
        Require(
            (await store.GetDeclarationsAsync(
                repositoryId,
                shared.SymbolId!,
                cancellationToken).ConfigureAwait(false)).Count == 1,
            "equivalent replacement must be idempotent");
        checks.Add("equivalent-replacement-idempotent");

        await store.ReplaceIndexUnitAsync(second, cancellationToken)
            .ConfigureAwait(false);
        Require(
            (await store.GetDeclarationsAsync(
                repositoryId,
                shared.SymbolId!,
                cancellationToken).ConfigureAwait(false)).Count == 2,
            "partial symbol declarations must coexist");
        await store.DeleteIndexUnitAsync(
            repositoryId,
            pluginId,
            first.Origin.IndexUnitId,
            cancellationToken).ConfigureAwait(false);
        Require(
            await store.GetNodeAsync(repositoryId, shared.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "shared semantic node must survive one contribution removal");
        Require(
            await store.GetNodeAsync(repositoryId, firstOnly.Id, cancellationToken)
                .ConfigureAwait(false) is null,
            "unit-owned node must be removed");
        Require(
            await store.GetNodeAsync(repositoryId, secondOnly.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "unrelated unit facts must survive deletion");
        checks.Add("owned-deletion-and-shared-node-survival");

        var beforeFailure = await store.GetNodeAsync(
            repositoryId,
            secondOnly.Id,
            cancellationToken).ConfigureAwait(false);
        await RequireThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await store.ReplaceIndexUnitAsync(
                Replacement(
                    repositoryId,
                    runId,
                    pluginId,
                    "unit:second",
                    [secondOnly],
                    edges:
                    [
                        Edge(
                            "invalid",
                            secondOnly.Id,
                            new CodeNodeId("node:missing"))
                    ]),
                cancellationToken).ConfigureAwait(false));
        Require(
            await store.GetNodeAsync(repositoryId, secondOnly.Id, cancellationToken)
                .ConfigureAwait(false) == beforeFailure,
            "failed replacement must leave prior unit intact");
        checks.Add("failed-replacement-atomic");

        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(async () =>
                await store.DeleteIndexUnitAsync(
                    repositoryId,
                    pluginId,
                    second.Origin.IndexUnitId,
                    source.Token).ConfigureAwait(false));
        }
        Require(
            await store.GetNodeAsync(repositoryId, secondOnly.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "cancelled mutation must leave prior unit intact");
        checks.Add("cancellation-atomic");

        var traversalNodes = new[]
        {
            Node("a", "Example.A"),
            Node("b", "Example.B"),
            Node("c", "Example.C"),
            Node("d", "Example.D")
        };
        await store.ReplaceIndexUnitAsync(
            Replacement(
                repositoryId,
                runId,
                pluginId,
                "unit:traversal",
                traversalNodes,
                edges:
                [
                    Edge("ab", traversalNodes[0].Id, traversalNodes[1].Id),
                    Edge("ac", traversalNodes[0].Id, traversalNodes[2].Id),
                    Edge("bd", traversalNodes[1].Id, traversalNodes[3].Id),
                    Edge("ca", traversalNodes[2].Id, traversalNodes[0].Id)
                ]),
            cancellationToken).ConfigureAwait(false);
        var bounded = await store.TraverseAsync(
            repositoryId,
            new CodeGraphTraversalQuery(
                traversalNodes[0].Id,
                maxDepth: 5,
                maxNodes: 3,
                maxEdges: 3),
            cancellationToken).ConfigureAwait(false);
        Require(bounded.Truncated, "bounded traversal must report truncation");
        Require(bounded.Nodes.Count == 3, "bounded traversal node limit");
        Require(bounded.Edges.Count <= 3, "bounded traversal edge limit");
        var boundedNodeIds = bounded.Nodes
            .Select(node => node.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        Require(
            bounded.Edges.All(edge =>
                boundedNodeIds.Contains(edge.SourceId.Value) &&
                boundedNodeIds.Contains(edge.TargetId.Value)),
            "traversal edges must have included endpoints");
        var repeated = await store.TraverseAsync(
            repositoryId,
            new CodeGraphTraversalQuery(
                traversalNodes[0].Id,
                maxDepth: 5,
                maxNodes: 3,
                maxEdges: 3),
            cancellationToken).ConfigureAwait(false);
        Require(
            bounded.Nodes.Select(node => node.Id.Value)
                .SequenceEqual(repeated.Nodes.Select(node => node.Id.Value)),
            "traversal ordering must be deterministic");
        checks.Add("bounded-deterministic-traversal");

        var completed = new CodeIndexRunManifest(
            repositoryId,
            runId,
            startedAt,
            CodeIndexRunStatus.Completed,
            startedAt.AddSeconds(1),
            [pluginId]);
        var indexState = new CodeRepositoryIndexState(
            repositoryId,
            runId,
            [new CodeSourceManifest(pluginId, "1.0.0", "src/Current.cs", "sha256:current")],
            "snapshot:conformance",
            true);
        await store.CompleteIndexRunAsync(completed, indexState, cancellationToken)
            .ConfigureAwait(false);
        await store.CompleteIndexRunAsync(
            completed,
            new CodeRepositoryIndexState(
                repositoryId,
                runId,
                [new CodeSourceManifest(pluginId, "1.0.0", "src/Current.cs", "sha256:current")],
                "snapshot:conformance",
                true),
            cancellationToken)
            .ConfigureAwait(false);
        Require(
            RunsEquivalent(
                await store.GetIndexRunAsync(repositoryId, runId, cancellationToken)
                    .ConfigureAwait(false),
                completed),
            "index run transition and round-trip");
        checks.Add("index-run-transition");
        var restoredState = await store.GetLatestIndexStateAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        Require(
            restoredState is not null &&
            restoredState.IndexRunId == runId &&
            restoredState.SnapshotIdentity == "snapshot:conformance" &&
            restoredState.IsConsistentSnapshot &&
            restoredState.Sources.Count == 1 &&
            restoredState.Sources[0].SourcePath == "src/Current.cs",
            "successful source state round-trip");
        checks.Add("successful-source-state-round-trip");

        var failedRunId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
        var failedRunning = new CodeIndexRunManifest(
            repositoryId,
            failedRunId,
            startedAt.AddSeconds(2),
            plugins: [pluginId]);
        await store.StoreIndexRunAsync(failedRunning, cancellationToken)
            .ConfigureAwait(false);
        await store.StoreIndexRunAsync(
            new CodeIndexRunManifest(
                repositoryId,
                failedRunId,
                failedRunning.StartedAt,
                CodeIndexRunStatus.Failed,
                failedRunning.StartedAt.AddSeconds(1),
                [pluginId]),
            cancellationToken).ConfigureAwait(false);
        var stateAfterFailure = await store.GetLatestIndexStateAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        Require(
            stateAfterFailure?.IndexRunId == runId,
            "failed runs must retain the last successful source state");
        checks.Add("failed-run-retains-source-state");

        await RequireThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await store.ReplaceIndexUnitAsync(
                Replacement(
                    repositoryId,
                    runId,
                    pluginId,
                    "unit:after-completion",
                    [Node("late", "Example.Late")]),
                cancellationToken).ConfigureAwait(false));
        Require(
            await store.GetNodeAsync(
                repositoryId,
                new CodeNodeId("node:late"),
                cancellationToken).ConfigureAwait(false) is null,
            "terminal index runs must reject late facts");
        checks.Add("terminal-run-rejects-late-facts");

        return new CodeGraphStoreConformanceReport(checks);
    }

    private static CodeGraphNode Node(string id, string qualifiedName) =>
        new(
            new CodeNodeId($"node:{id}"),
            CodeNodeKinds.Type,
            qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..],
            qualifiedName,
            new CodeSymbolId($"symbol:{id}"));

    private static CodeGraphDeclaration Declaration(
        string id,
        CodeGraphNode symbol,
        string path) =>
        new(
            new CodeDeclarationId($"declaration:{id}"),
            symbol.SymbolId!,
            symbol.Id,
            new CodeLocation(path, 1, 1, 2, 1));

    private static CodeGraphEdge Edge(
        string id,
        CodeNodeId source,
        CodeNodeId target) =>
        new(
            new CodeEdgeId($"edge:{id}"),
            source,
            target,
            CodeEdgeKinds.Calls,
            new CodeEvidence(CodeEvidenceKind.Semantic, "conformance"));

    private static CodeIndexUnitReplacement Replacement(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CodePluginId pluginId,
        string unitId,
        IReadOnlyList<CodeGraphNode> nodes,
        IReadOnlyList<CodeGraphDeclaration>? declarations = null,
        IReadOnlyList<CodeGraphEdge>? edges = null) =>
        new(
            new CodeFactOrigin(
                repositoryId,
                pluginId,
                "1.0.0",
                runId,
                new CodeIndexUnitId(unitId)),
            nodes,
            declarations,
            edges);

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new CodeGraphStoreConformanceException(description);
    }

    private static bool RunsEquivalent(
        CodeIndexRunManifest? first,
        CodeIndexRunManifest second) =>
        first is not null &&
        first.RepositoryId == second.RepositoryId &&
        first.Id == second.Id &&
        first.StartedAt == second.StartedAt &&
        first.Status == second.Status &&
        first.CompletedAt == second.CompletedAt &&
        first.Plugins.Count == second.Plugins.Count &&
        first.Plugins.All(second.Plugins.Contains);

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new CodeGraphStoreConformanceException(
            $"Expected {typeof(TException).Name}.");
    }
}
