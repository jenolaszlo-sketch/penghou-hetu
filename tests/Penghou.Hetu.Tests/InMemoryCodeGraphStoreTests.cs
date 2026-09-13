using Penghou.Hetu.Testing;

namespace Penghou.Hetu.Tests;

public sealed class InMemoryCodeGraphStoreTests
{
    [Fact]
    public async Task Store_PassesProviderConformanceSuite()
    {
        var report = await CodeGraphStoreConformanceSuite.VerifyAsync(
            new Fixture(),
            CancellationToken.None);

        Assert.Contains("equivalent-replacement-idempotent", report.PassedChecks);
        Assert.Contains("owned-deletion-and-shared-node-survival", report.PassedChecks);
        Assert.Contains("failed-replacement-atomic", report.PassedChecks);
        Assert.Contains("bounded-deterministic-traversal", report.PassedChecks);
        Assert.Contains("superseded-run-completion-conflicts", report.PassedChecks);
        Assert.Contains("fresh-run-completes-after-conflict", report.PassedChecks);
        Assert.Contains("traversal-relationship-kind-filter", report.PassedChecks);
        Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
        Assert.Contains("failed-run-retains-source-state", report.PassedChecks);
    }

    [Fact]
    public async Task StageIndexUnitDeletionAsync_RejectsDanglingReferences()
    {
        var store = new InMemoryCodeGraphStore();
        var repositoryId = new CodeRepositoryId("repo:delete-guard");
        var pluginId = new CodePluginId("plugin:delete-guard");
        var runId = new CodeIndexRunId("run:delete-guard");
        var started = DateTimeOffset.UtcNow;
        await store.UpsertRepositoryAsync(new(repositoryId));
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        var target = new CodeGraphNode(
            new("node:target"), CodeNodeKinds.Type, "Target", "Example.Target", new("symbol:target"));
        var referrer = new CodeGraphNode(
            new("node:referrer"), CodeNodeKinds.Type, "Referrer", "Example.Referrer", new("symbol:referrer"));
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:target")),
            [target]));
        await store.StageIndexUnitAsync(new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:referrer")),
            [referrer],
            edges:
            [new CodeGraphEdge(
                new("edge:refers"),
                referrer.Id,
                target.Id,
                CodeEdgeKinds.References,
                new CodeEvidence(CodeEvidenceKind.Semantic, "tests"))]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, runId, []));

        var deleting = new CodeIndexRunId("run:delete-guard-next");
        await store.StoreIndexRunAsync(new(repositoryId, deleting, started.AddSeconds(2), plugins: [pluginId]));
        await Assert.ThrowsAsync<CodeGraphBatchRejectedException>(() => store.StageIndexUnitDeletionAsync(
            repositoryId, deleting, pluginId, new("unit:target")).AsTask());
        Assert.NotNull(await store.GetNodeAsync(repositoryId, target.Id));
    }

    private sealed class Fixture : ICodeGraphStoreFixture
    {
        public ICodeGraphStore CreateStore() => new InMemoryCodeGraphStore();
    }
}
