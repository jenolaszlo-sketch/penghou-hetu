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

        await RequireThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await store.StageIndexUnitAsync(
                Replacement(
                    repositoryId,
                    runId,
                    new CodePluginId("plugin:not-in-run"),
                    "unit:foreign",
                    [Node("foreign", "Example.Foreign")]),
                cancellationToken).ConfigureAwait(false));
        checks.Add("replacement-plugin-must-belong-to-run");

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

        await store.StageIndexUnitAsync(first, cancellationToken)
            .ConfigureAwait(false);
        await store.StageIndexUnitAsync(first, cancellationToken)
            .ConfigureAwait(false);
        Require(
            (await store.GetDeclarationsAsync(
                repositoryId,
                shared.SymbolId!,
                cancellationToken).ConfigureAwait(false)).Count == 0,
            "staged replacements must not be query-visible");
        checks.Add("staged-replacement-invisible");
        checks.Add("equivalent-replacement-idempotent");

        await store.StageIndexUnitAsync(second, cancellationToken)
            .ConfigureAwait(false);
        var initialCompleted = new CodeIndexRunManifest(
            repositoryId,
            runId,
            startedAt,
            CodeIndexRunStatus.Completed,
            startedAt.AddSeconds(1),
            [pluginId]);
        await store.CompleteIndexRunAsync(
            initialCompleted,
            new CodeRepositoryIndexState(repositoryId, runId, []),
            cancellationToken).ConfigureAwait(false);
        Require(
            (await store.GetDeclarationsAsync(
                repositoryId,
                shared.SymbolId!,
                cancellationToken).ConfigureAwait(false)).Count == 2,
            "partial symbol declarations must coexist");
        checks.Add("atomic-publication-makes-staged-facts-visible");
        var sharedEnvelope = await store.FindNodesByQualifiedNameWithProvenanceAsync(
            repositoryId,
            shared.QualifiedName!,
            cancellationToken).ConfigureAwait(false);
        Require(
            sharedEnvelope is not null &&
            sharedEnvelope.Publication.IndexRunId == runId &&
            sharedEnvelope.Query.Operation == "qualified-name" &&
            sharedEnvelope.Result.Count == 1,
            "qualified-name provenance must identify its publication and applied query");
        var sharedProvenance = sharedEnvelope!.Provenance.Single(value =>
            value.Kind == CodeGraphFactKind.Node && value.FactId == shared.Id.Value);
        Require(
            sharedProvenance.Contributors.Count == 2 &&
            sharedProvenance.Contributors.Select(origin => origin.IndexUnitId)
                .ToHashSet().SetEquals([first.Origin.IndexUnitId, second.Origin.IndexUnitId]),
            "shared facts must retain every contributing index-unit origin");
        checks.Add("published-query-provenance");
        checks.Add("shared-fact-multiple-contributors");
        var declarationEnvelope = await store.GetDeclarationsWithProvenanceAsync(
            repositoryId,
            shared.SymbolId!,
            cancellationToken).ConfigureAwait(false);
        Require(
            declarationEnvelope is not null &&
            declarationEnvelope.Result.Count == 2 &&
            declarationEnvelope.Provenance.Count == 2 &&
            declarationEnvelope.Provenance.All(value =>
                value.Kind == CodeGraphFactKind.Declaration &&
                value.Contributors.Count == 1),
            "declaration queries must trace every returned declaration");
        checks.Add("declaration-provenance");

        var updateRunId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
        var updateStartedAt = startedAt.AddSeconds(2);
        await store.StoreIndexRunAsync(
            new CodeIndexRunManifest(
                repositoryId,
                updateRunId,
                updateStartedAt,
                plugins: [pluginId]),
            cancellationToken).ConfigureAwait(false);
        await store.StageIndexUnitDeletionAsync(
            repositoryId,
            updateRunId,
            pluginId,
            first.Origin.IndexUnitId,
            cancellationToken).ConfigureAwait(false);
        Require(
            await store.GetNodeAsync(repositoryId, shared.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "shared semantic node must survive one contribution removal");
        Require(
            await store.GetNodeAsync(repositoryId, firstOnly.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "staged deletion must not be visible before publication");
        Require(
            await store.GetNodeAsync(repositoryId, secondOnly.Id, cancellationToken)
                .ConfigureAwait(false) is not null,
            "unrelated unit facts must survive deletion");
        var beforeFailure = await store.GetNodeAsync(
            repositoryId,
            secondOnly.Id,
            cancellationToken).ConfigureAwait(false);
        await RequireThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await store.StageIndexUnitAsync(
                Replacement(
                    repositoryId,
                    updateRunId,
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
                await store.StageIndexUnitDeletionAsync(
                    repositoryId,
                    updateRunId,
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
        await store.StageIndexUnitAsync(
            Replacement(
                repositoryId,
                updateRunId,
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
        var completed = new CodeIndexRunManifest(
            repositoryId,
            updateRunId,
            updateStartedAt,
            CodeIndexRunStatus.Completed,
            updateStartedAt.AddSeconds(1),
            [pluginId]);
        var indexState = new CodeRepositoryIndexState(
            repositoryId,
            updateRunId,
            [new CodeSourceManifest(pluginId, "1.0.0", "src/Current.cs", "sha256:current")],
            "snapshot:conformance",
            true);
        await store.CompleteIndexRunAsync(completed, indexState, cancellationToken)
            .ConfigureAwait(false);
        var bounded = await store.TraverseAsync(
            repositoryId,
            new CodeGraphTraversalQuery(
                traversalNodes[0].Id,
                maxDepth: 5,
                maxNodes: 3,
                maxEdges: 3),
            cancellationToken).ConfigureAwait(false);
        Require(bounded.Truncated, "bounded traversal must report truncation");
        Require(
            bounded.TruncationReason != CodeGraphTruncationReason.None &&
            bounded.NodesExamined > 0 &&
            bounded.EdgesExamined > 0 &&
            bounded.DepthReached >= 0,
            "bounded traversal must explain truncation and report examined counts");
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
        checks.Add("traversal-truncation-diagnostics");
        var traversalEnvelope = await store.TraverseWithProvenanceAsync(
            repositoryId,
            new CodeGraphTraversalQuery(
                traversalNodes[0].Id,
                maxDepth: 5,
                maxNodes: 3,
                maxEdges: 3),
            cancellationToken).ConfigureAwait(false);
        Require(
            traversalEnvelope is not null &&
            traversalEnvelope.Publication.IndexRunId == updateRunId &&
            traversalEnvelope.Query.Traversal is not null &&
            traversalEnvelope.Provenance.Count ==
                traversalEnvelope.Result.Nodes.Count + traversalEnvelope.Result.Edges.Count &&
            traversalEnvelope.Provenance.All(value => value.Contributors.Count > 0),
            "traversal provenance must cover every returned node and edge");
        checks.Add("bounded-traversal-provenance");

        Require(
            await store.GetNodeAsync(repositoryId, firstOnly.Id, cancellationToken)
                .ConfigureAwait(false) is null,
            "published deletion must remove unit-owned facts");
        checks.Add("owned-deletion-and-shared-node-survival");
        await store.CompleteIndexRunAsync(
            completed,
            new CodeRepositoryIndexState(
                repositoryId,
                updateRunId,
                [new CodeSourceManifest(pluginId, "1.0.0", "src/Current.cs", "sha256:current")],
                "snapshot:conformance",
                true),
            cancellationToken)
            .ConfigureAwait(false);
        Require(
            RunsEquivalent(
                await store.GetIndexRunAsync(repositoryId, updateRunId, cancellationToken)
                    .ConfigureAwait(false),
                completed),
            "index run transition and round-trip");
        checks.Add("index-run-transition");
        var restoredState = await store.GetLatestIndexStateAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        var restoredPublication = await store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        Require(
            restoredState is not null &&
            restoredState.IndexRunId == updateRunId &&
            restoredState.SnapshotIdentity == "snapshot:conformance" &&
            restoredState.IsConsistentSnapshot &&
            restoredState.Sources.Count == 1 &&
            restoredState.Sources[0].SourcePath == "src/Current.cs",
            "successful source state round-trip");
        checks.Add("successful-source-state-round-trip");
        Require(
            restoredPublication is not null &&
            restoredPublication.RepositoryId == restoredState!.RepositoryId &&
            restoredPublication.IndexRunId == restoredState.IndexRunId &&
            restoredPublication.SnapshotIdentity == restoredState.SnapshotIdentity &&
            restoredPublication.IsConsistentSnapshot == restoredState.IsConsistentSnapshot &&
            restoredPublication.IndexIdentity == restoredState.IndexIdentity,
            "latest publication must exactly describe the successful source state");
        checks.Add("latest-publication-round-trip");

        var failedRunId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
        var failedRunning = new CodeIndexRunManifest(
            repositoryId,
            failedRunId,
            startedAt.AddSeconds(2),
            plugins: [pluginId]);
        await store.StoreIndexRunAsync(failedRunning, cancellationToken)
            .ConfigureAwait(false);
        var failedNode = Node("failed-stage", "Example.FailedStage");
        await store.StageIndexUnitAsync(
            Replacement(
                repositoryId,
                failedRunId,
                pluginId,
                "unit:failed-stage",
                [failedNode]),
            cancellationToken).ConfigureAwait(false);
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
            stateAfterFailure?.IndexRunId == updateRunId,
            "failed runs must retain the last successful source state");
        Require(
            (await store.GetLatestPublicationAsync(repositoryId, cancellationToken)
                .ConfigureAwait(false))?.IndexRunId == updateRunId,
            "failed runs must retain the last successful publication");
        Require(
            await store.GetNodeAsync(repositoryId, failedNode.Id, cancellationToken)
                .ConfigureAwait(false) is null,
            "failed runs must discard staged graph changes");
        checks.Add("failed-run-retains-source-state");
        checks.Add("failed-run-discards-staged-graph");

        var cancelledRunId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
        var cancelledRunning = new CodeIndexRunManifest(
            repositoryId,
            cancelledRunId,
            startedAt.AddSeconds(4),
            plugins: [pluginId]);
        var cancelledNode = Node("cancelled-stage", "Example.CancelledStage");
        await store.StoreIndexRunAsync(cancelledRunning, cancellationToken)
            .ConfigureAwait(false);
        await store.StageIndexUnitAsync(
            Replacement(
                repositoryId,
                cancelledRunId,
                pluginId,
                "unit:cancelled-stage",
                [cancelledNode]),
            cancellationToken).ConfigureAwait(false);
        await store.StoreIndexRunAsync(
            new CodeIndexRunManifest(
                repositoryId,
                cancelledRunId,
                cancelledRunning.StartedAt,
                CodeIndexRunStatus.Cancelled,
                cancelledRunning.StartedAt.AddSeconds(1),
                [pluginId]),
            cancellationToken).ConfigureAwait(false);
        Require(
            await store.GetNodeAsync(repositoryId, cancelledNode.Id, cancellationToken)
                .ConfigureAwait(false) is null,
            "cancelled runs must discard staged graph changes");
        Require(
            (await store.GetLatestIndexStateAsync(repositoryId, cancellationToken)
                .ConfigureAwait(false))?.IndexRunId == updateRunId,
            "cancelled runs must retain the last successful source state");
        Require(
            (await store.GetLatestPublicationAsync(repositoryId, cancellationToken)
                .ConfigureAwait(false))?.IndexRunId == updateRunId,
            "cancelled runs must retain the last successful publication");
        checks.Add("cancelled-run-discards-staged-graph");

        await RequireThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await store.StageIndexUnitAsync(
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
