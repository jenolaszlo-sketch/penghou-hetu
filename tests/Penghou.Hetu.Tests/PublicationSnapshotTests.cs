namespace Penghou.Hetu.Tests;

public sealed class PublicationSnapshotTests
{
    [Fact]
    public async Task Snapshot_RoundTripReproducesSupersededPublication()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:snapshot");
        var pluginId = new CodePluginId("plugin:snapshot");
        await store.UpsertRepositoryAsync(new CodeRepositoryManifest(repositoryId));
        var run1 = new CodeIndexRunId("run:v1");
        var started = DateTimeOffset.UtcNow;
        await store.StoreIndexRunAsync(new(repositoryId, run1, started, plugins: [pluginId]));
        var nodeA = Node("a", "Example.A");
        var nodeB = Node("b", "Example.B");
        await store.StageIndexUnitAsync(
            new CodeIndexUnitReplacement(
                new CodeFactOrigin(repositoryId, pluginId, "1.0.0", run1, new("unit:one")),
                [nodeA, nodeB],
                edges: [Edge("ab", nodeA.Id, nodeB.Id)]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, run1, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, run1, [Source("src/A.cs")], "snapshot:v1", true));

        var snapshot = await CodePublicationSnapshot.ExportAsync(store, repositoryId);
        var original = await store.GetLatestPublicationAsync(repositoryId);
        Assert.NotNull(original);

        var run2 = new CodeIndexRunId("run:v2");
        await store.StoreIndexRunAsync(new(repositoryId, run2, started.AddSeconds(2), plugins: [pluginId]));
        await store.StageIndexUnitAsync(
            new CodeIndexUnitReplacement(
                new CodeFactOrigin(repositoryId, pluginId, "1.0.0", run2, new("unit:one")),
                [nodeB]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, run2, started.AddSeconds(2), CodeIndexRunStatus.Completed, started.AddSeconds(3), [pluginId]),
            new(repositoryId, run2, [Source("src/A.cs")], "snapshot:v2", true));
        Assert.Empty(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.A"));

        var restored = new InMemoryCodeGraphStore();
        await snapshot.ImportAsync(restored);
        var publication = await restored.GetLatestPublicationAsync(repositoryId);
        Assert.Equal(original, publication);
        Assert.Single(await restored.GetPublishedUnitsAsync(repositoryId));
        var names = await restored.FindNodesByQualifiedNameAsync(repositoryId, "Example.A");
        Assert.Single(names);
        Assert.Equal(nodeA.Id, names[0].Id);
        var traversal = await restored.TraverseAsync(
            repositoryId,
            new CodeGraphTraversalQuery(nodeA.Id, maxDepth: 5, maxNodes: 10, maxEdges: 10));
        Assert.Contains(traversal.Edges, edge => edge.Kind == CodeEdgeKinds.Calls);
    }

    [Fact]
    public async Task Snapshot_TamperedContentIsRejected()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:tamper");
        var pluginId = new CodePluginId("plugin:tamper");
        await store.UpsertRepositoryAsync(new CodeRepositoryManifest(repositoryId));
        var runId = new CodeIndexRunId("run:v1");
        var started = DateTimeOffset.UtcNow;
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var node = Node("a", "Example.A");
        await store.StageIndexUnitAsync(
            new CodeIndexUnitReplacement(
                new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:one")),
                [node]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, runId, [Source("src/A.cs", "plugin:tamper")], "snapshot:v1", true));

        var snapshot = await CodePublicationSnapshot.ExportAsync(store, repositoryId);
        var tampered = snapshot with
        {
            Units =
            [
                new CodeIndexUnitReplacement(snapshot.Units[0].Origin, [Node("evil", "Example.Evil")])
            ]
        };
        await Assert.ThrowsAsync<CodePublicationSnapshotException>(
            () => tampered.ImportAsync(new InMemoryCodeGraphStore()).AsTask());
        var wrongSchema = snapshot with { SchemaVersion = 999 };
        await Assert.ThrowsAsync<CodePublicationSnapshotException>(
            () => wrongSchema.ImportAsync(new InMemoryCodeGraphStore()).AsTask());
    }

    [Fact]
    public async Task Snapshot_BoundsAndMissingPublicationFailExplicitly()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:bounds");
        var pluginId = new CodePluginId("plugin:bounds");
        await store.UpsertRepositoryAsync(new CodeRepositoryManifest(repositoryId));
        var runId = new CodeIndexRunId("run:v1");
        var started = DateTimeOffset.UtcNow;
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        await store.StageIndexUnitAsync(
            new CodeIndexUnitReplacement(
                new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:one")),
                [Node("a", "Example.A")]));
        await store.StageIndexUnitAsync(
            new CodeIndexUnitReplacement(
                new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:two")),
                [Node("b", "Example.B")]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, runId, [Source("src/A.cs", "plugin:bounds")], "snapshot:v1", true));

        await Assert.ThrowsAsync<CodePublicationSnapshotException>(
            () => CodePublicationSnapshot.ExportAsync(
                store, repositoryId, new CodeSnapshotOptions { MaxUnits = 1 }).AsTask());
        await Assert.ThrowsAsync<CodePublicationSnapshotException>(
            () => CodePublicationSnapshot.ExportAsync(
                new InMemoryCodeGraphStore(), repositoryId).AsTask());
    }

    private static CodeGraphNode Node(string id, string qualifiedName) =>
        new(
            new CodeNodeId($"node:{id}"),
            CodeNodeKinds.Type,
            qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..],
            qualifiedName,
            new CodeSymbolId($"symbol:{id}"));

    private static CodeGraphEdge Edge(string id, CodeNodeId source, CodeNodeId target) =>
        new(
            new CodeEdgeId($"edge:{id}"),
            source,
            target,
            CodeEdgeKinds.Calls,
            new CodeEvidence(CodeEvidenceKind.Semantic, "snapshot"));

    private static CodeSourceManifest Source(string path, string plugin = "plugin:snapshot") =>
        new(new CodePluginId(plugin), "1.0.0", path, "sha256:test");
}
