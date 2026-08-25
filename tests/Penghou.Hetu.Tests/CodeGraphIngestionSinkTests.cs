namespace Penghou.Hetu.Tests;

public sealed class CodeGraphIngestionSinkTests
{
    [Fact]
    public async Task CompleteAsync_RejectsAnIncompleteBufferedUnit()
    {
        var setup = await SetupAsync();
        await using var sink = new CodeGraphIngestionSink(setup.Store);
        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [Node("pending")],
            completesIndexUnit: false));

        var exception = await Assert.ThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await sink.CompleteAsync());

        Assert.Contains(
            exception.Errors,
            error => error.Code == "ingestion.index-unit.incomplete");
        Assert.Null(await setup.Store.GetNodeAsync(
            setup.Origin.RepositoryId,
            new CodeNodeId("node:pending")));
    }

    [Fact]
    public async Task IncompleteBatches_AreNotVisibleBeforeCompletion()
    {
        var setup = await SetupAsync();
        await using var sink = new CodeGraphIngestionSink(setup.Store);
        var node = Node("one");

        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [node],
            completesIndexUnit: false));

        Assert.Null(await setup.Store.GetNodeAsync(setup.RepositoryId, node.Id));

        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            completesIndexUnit: true));

        Assert.NotNull(await setup.Store.GetNodeAsync(setup.RepositoryId, node.Id));
    }

    [Fact]
    public async Task InvalidFinalBatch_DoesNotReplaceExistingUnit()
    {
        var setup = await SetupAsync();
        var original = Node("original");
        await setup.Store.ReplaceIndexUnitAsync(
            new CodeIndexUnitReplacement(setup.Origin, [original]));
        await using var sink = new CodeGraphIngestionSink(
            setup.Store,
            new CodeGraphBatchLimits(maxNodes: 1));

        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [Node("replacement")],
            completesIndexUnit: false));
        await Assert.ThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await sink.WriteBatchAsync(new CodeGraphBatch(
                setup.Origin,
                nodes: [Node("two"), Node("three")],
                completesIndexUnit: true)));

        Assert.NotNull(await setup.Store.GetNodeAsync(
            setup.RepositoryId,
            original.Id));
        Assert.Null(await setup.Store.GetNodeAsync(
            setup.RepositoryId,
            new CodeNodeId("node:replacement")));
    }

    [Fact]
    public async Task RepeatedSmallBatches_CannotEvadeIndexUnitLimit()
    {
        var setup = await SetupAsync();
        await using var sink = new CodeGraphIngestionSink(
            setup.Store,
            new CodeGraphBatchLimits(
                maxBatchesPerIndexUnit: 2,
                maxFactsPerIndexUnit: 2));
        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [Node("one")],
            completesIndexUnit: false));
        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [Node("two")],
            completesIndexUnit: false));

        var exception = await Assert.ThrowsAsync<CodeGraphBatchRejectedException>(
            async () => await sink.WriteBatchAsync(new CodeGraphBatch(
                setup.Origin,
                completesIndexUnit: true)));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "ingestion.index-unit.limit");
    }

    [Fact]
    public async Task Completion_EmitsPrivacySafeCounts()
    {
        var setup = await SetupAsync();
        CodeGraphIngestionDiagnostics? diagnostics = null;
        await using var sink = new CodeGraphIngestionSink(
            setup.Store,
            onCompleted: value => diagnostics = value);

        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [Node("one")],
            completesIndexUnit: true));

        Assert.NotNull(diagnostics);
        Assert.Equal(1, diagnostics.NodesReceived);
        Assert.Equal(1, diagnostics.BatchesReceived);
        Assert.Empty(diagnostics.WarningCodes);
    }

    [Fact]
    public async Task DiagnosticsFailure_DoesNotChangeCommittedResult()
    {
        var setup = await SetupAsync();
        await using var sink = new CodeGraphIngestionSink(
            setup.Store,
            onCompleted: _ => throw new InvalidOperationException("telemetry failed"));
        var node = Node("one");

        await sink.WriteBatchAsync(new CodeGraphBatch(
            setup.Origin,
            nodes: [node],
            completesIndexUnit: true));

        Assert.NotNull(await setup.Store.GetNodeAsync(setup.RepositoryId, node.Id));
    }

    [Fact]
    public void Validator_RejectsDuplicateFactsAndOversizedText()
    {
        var node = Node(
            "duplicate",
            new Dictionary<string, CodePropertyValue>
            {
                ["text"] = new CodeTextProperty("too long")
            });
        var batch = new CodeGraphBatch(
            Origin(),
            nodes: [node, node]);

        var errors = new CodeGraphBatchValidator().Validate(
            batch,
            new CodeGraphBatchLimits(maxTextPropertyLength: 3));

        Assert.Contains(errors, error => error.Code == "batch.node.duplicate");
        Assert.Contains(errors, error => error.Code == "batch.property.text-length");
    }

    private static async Task<Setup> SetupAsync()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId($"repo:{Guid.NewGuid():N}");
        var origin = Origin(repositoryId);
        await store.UpsertRepositoryAsync(new CodeRepositoryManifest(repositoryId));
        await store.StoreIndexRunAsync(new CodeIndexRunManifest(
            repositoryId,
            origin.IndexRunId,
            DateTimeOffset.UtcNow,
            plugins: [origin.PluginId]));
        return new Setup(store, repositoryId, origin);
    }

    private static CodeFactOrigin Origin(CodeRepositoryId? repositoryId = null) =>
        new(
            repositoryId ?? new CodeRepositoryId("repo:test"),
            new CodePluginId("plugin:test"),
            "1.0.0",
            new CodeIndexRunId("run:test"),
            new CodeIndexUnitId("unit:test"));

    private static CodeGraphNode Node(
        string id,
        IReadOnlyDictionary<string, CodePropertyValue>? properties = null) =>
        new(
            new CodeNodeId($"node:{id}"),
            CodeNodeKinds.Type,
            id,
            $"Example.{id}",
            new CodeSymbolId($"symbol:{id}"),
            properties);

    private sealed record Setup(
        InMemoryCodeGraphStore Store,
        CodeRepositoryId RepositoryId,
        CodeFactOrigin Origin);
}
