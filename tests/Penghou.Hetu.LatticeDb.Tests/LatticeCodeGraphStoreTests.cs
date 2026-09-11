using LatticeDbSharp;
using Penghou.Hetu.Testing;

namespace Penghou.Hetu.LatticeDb.Tests;

public sealed class LatticeCodeGraphStoreTests
{
    [Fact]
    public void PublicApi_IsIntentional()
    {
        Assert.Equal(
            [
                typeof(LatticeCodeGraphSchemaException),
                typeof(LatticeCodeGraphStore),
                typeof(HetuHostBuilderExtensions),
                typeof(LatticeDbStoreOptions)
            ],
            typeof(LatticeCodeGraphStore).Assembly.GetExportedTypes()
                .OrderBy(type => type.FullName, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Store_PassesProviderConformanceSuite()
    {
        var path = TemporaryDatabasePath();
        var fixture = new Fixture(path);
        try
        {
            var report = await CodeGraphStoreConformanceSuite.VerifyAsync(fixture);

            Assert.Contains("bounded-deterministic-traversal", report.PassedChecks);
            Assert.Contains("traversal-relationship-kind-filter", report.PassedChecks);
            Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
            Assert.Contains("latest-publication-round-trip", report.PassedChecks);
            Assert.True(fixture.Store!.CheckHealth().IsHealthy);
            var health = await ((ICodeGraphStoreHealthCheck)fixture.Store)
                .CheckHealthAsync();
            Assert.Equal(CodeGraphStoreHealthStatus.Healthy, health.Status);
            Assert.Equal("lattice", health.StoreName);
        }
        finally
        {
            fixture.Store?.Dispose();
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ReopensDurableState()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:durable");
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(
                    repositoryId,
                    "Durable repository",
                    "repo://durable"));
            }

            using var reopened = new LatticeCodeGraphStore(path);
            var repository = await reopened.GetRepositoryAsync(repositoryId);

            Assert.NotNull(repository);
            Assert.Equal("Durable repository", repository.DisplayName);
            Assert.True(reopened.CheckHealth().IsHealthy);
            Assert.Equal(1, reopened.CheckHealth().RepositoryCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ReopensCompletedRunIndexStateAndGraphFacts()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:full-reopen");
        var runId = new CodeIndexRunId("run:full-reopen");
        var pluginId = new CodePluginId("plugin:full-reopen");
        var node = new CodeGraphNode(
            new CodeNodeId("node:durable"),
            CodeNodeKinds.Type,
            "Durable",
            "Example.Durable",
            new CodeSymbolId("symbol:durable"));
        var started = DateTimeOffset.UtcNow;
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
                await first.StageIndexUnitAsync(new(
                    new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:durable")),
                    [node]));
                await first.CompleteIndexRunAsync(
                    new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                    new(repositoryId, runId, [new CodeSourceManifest(pluginId, "1.0.0", "src/Durable.cs", "sha256:durable")]));
            }

            using var reopened = new LatticeCodeGraphStore(path);

            var restoredNode = await reopened.GetNodeAsync(repositoryId, node.Id);
            Assert.NotNull(restoredNode);
            Assert.Equal(node.Id, restoredNode.Id);
            Assert.Equal(node.SymbolId, restoredNode.SymbolId);
            Assert.Equal(node.QualifiedName, restoredNode.QualifiedName);
            Assert.Equal(CodeIndexRunStatus.Completed, (await reopened.GetIndexRunAsync(repositoryId, runId))!.Status);
            Assert.Equal(runId, (await reopened.GetLatestIndexStateAsync(repositoryId))!.IndexRunId);
            Assert.Equal(1, reopened.CheckHealth().IndexUnitCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ReopensHistoricalAndLatestCompletedRuns()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:completed-history");
        var firstRunId = new CodeIndexRunId("run:completed-history:first");
        var secondRunId = new CodeIndexRunId("run:completed-history:second");
        var started = DateTimeOffset.UtcNow;
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await CompleteAsync(first, firstRunId, started);
                await CompleteAsync(first, secondRunId, started.AddSeconds(2));
            }

            using var reopened = new LatticeCodeGraphStore(path);

            Assert.Equal(
                CodeIndexRunStatus.Completed,
                (await reopened.GetIndexRunAsync(repositoryId, firstRunId))!.Status);
            Assert.Equal(
                CodeIndexRunStatus.Completed,
                (await reopened.GetIndexRunAsync(repositoryId, secondRunId))!.Status);
            Assert.Equal(
                secondRunId,
                (await reopened.GetLatestIndexStateAsync(repositoryId))!.IndexRunId);
        }
        finally
        {
            DeleteDatabase(path);
        }

        async Task CompleteAsync(
            LatticeCodeGraphStore store,
            CodeIndexRunId runId,
            DateTimeOffset runStarted)
        {
            await store.StoreIndexRunAsync(new(
                repositoryId,
                runId,
                runStarted));
            await store.CompleteIndexRunAsync(
                new(
                    repositoryId,
                    runId,
                    runStarted,
                    CodeIndexRunStatus.Completed,
                    runStarted.AddSeconds(1)),
                new(repositoryId, runId, []));
        }
    }

    [Fact]
    public async Task Store_ReopensStagedRunWithoutPublishingItAndCanResumePublication()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:staged-reopen");
        var runId = new CodeIndexRunId("run:staged-reopen");
        var pluginId = new CodePluginId("plugin:staged-reopen");
        var node = new CodeGraphNode(
            new("node:staged-reopen"),
            CodeNodeKinds.Type,
            "StagedReopen");
        var started = DateTimeOffset.UtcNow;
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
                await first.StageIndexUnitAsync(new(
                    new(repositoryId, pluginId, "1.0.0", runId, new("unit:staged-reopen")),
                    [node]));
                Assert.Null(await first.GetNodeAsync(repositoryId, node.Id));
            }

            using var reopened = new LatticeCodeGraphStore(path);
            Assert.Null(await reopened.GetNodeAsync(repositoryId, node.Id));
            await reopened.CompleteIndexRunAsync(
                new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                new(repositoryId, runId, []));
            Assert.NotNull(await reopened.GetNodeAsync(repositoryId, node.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void Store_RejectsIncompatibleSchemaVersion()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using (var store = new LatticeCodeGraphStore(path))
            {
            }

            using (var database = LatticeDatabase.Open(path, new LatticeDatabaseOptions { Create = false }))
            using (var txn = database.BeginWriteTransaction())
            {
                using var query = database.Prepare("MATCH (s:HetuMetadata) SET s.schemaVersion = 999");
                using var result = query.Execute(txn);
                result.ReadAll();
                txn.Commit();
            }

            var exception = Assert.Throws<LatticeCodeGraphSchemaException>(
                () => new LatticeCodeGraphStore(path));
            Assert.Equal(999, exception.ActualVersion);
            Assert.Equal(LatticeCodeGraphStore.CurrentSchemaVersion, exception.ExpectedVersion);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_RejectsCorruptedDurablePayloadOnReopen()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
                await first.UpsertRepositoryAsync(new(new CodeRepositoryId("repo:corrupt")));
            using (var database = LatticeDatabase.Open(path, new LatticeDatabaseOptions { Create = false }))
            using (var txn = database.BeginWriteTransaction())
            {
                using var query = database.Prepare("MATCH (s:HetuRepository) SET s.payload = 'not-base64'");
                using var result = query.Execute(txn);
                result.ReadAll();
                txn.Commit();
            }

            Assert.Throws<FormatException>(() => new LatticeCodeGraphStore(path));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_RollsBackInterruptedNativeTransactionAndReopensPriorUnit()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:rollback");
        var runId = new CodeIndexRunId("run:rollback");
        var updateRunId = new CodeIndexRunId("run:rollback:update");
        var pluginId = new CodePluginId("plugin:rollback");
        var started = DateTimeOffset.UtcNow;
        var prior = new CodeGraphNode(new("node:prior"), CodeNodeKinds.Type, "Prior");
        var replacement = new CodeGraphNode(new("node:replacement"), CodeNodeKinds.Type, "Replacement");
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
                await first.StageIndexUnitAsync(Unit(runId, [prior]));
                await first.CompleteIndexRunAsync(
                    new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                    new(repositoryId, runId, []));
                await first.StoreIndexRunAsync(new(
                    repositoryId,
                    updateRunId,
                    started.AddSeconds(2),
                    plugins: [pluginId]));
            }
            var failCommit = false;
            using (var interrupted = new LatticeCodeGraphStore(
                       path,
                       null,
                       point =>
                       {
                           if (failCommit && point == "before-commit")
                               throw new InjectedPersistenceException();
                       }))
            {
                await interrupted.StageIndexUnitAsync(Unit(updateRunId, [replacement]));
                Assert.NotNull(await interrupted.GetNodeAsync(repositoryId, prior.Id));
                Assert.Null(await interrupted.GetNodeAsync(repositoryId, replacement.Id));
                failCommit = true;
                await Assert.ThrowsAsync<InjectedPersistenceException>(async () =>
                    await interrupted.CompleteIndexRunAsync(
                        new(
                            repositoryId,
                            updateRunId,
                            started.AddSeconds(2),
                            CodeIndexRunStatus.Completed,
                            started.AddSeconds(3),
                            [pluginId]),
                        new(repositoryId, updateRunId, [])));
            }

            using var reopened = new LatticeCodeGraphStore(path);
            Assert.NotNull(await reopened.GetNodeAsync(repositoryId, prior.Id));
            Assert.Null(await reopened.GetNodeAsync(repositoryId, replacement.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }

        CodeIndexUnitReplacement Unit(
            CodeIndexRunId ownerRunId,
            IReadOnlyList<CodeGraphNode> nodes) => new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", ownerRunId, new("unit:rollback")),
            nodes);
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hetu-lattice-{Guid.NewGuid():N}.ltdb");

    [Fact]
    public async Task Store_OpensAsyncAndHonorsCancellation()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var store = await LatticeCodeGraphStore.OpenAsync(path);
            Assert.True(store.CheckHealth().IsHealthy);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await LatticeCodeGraphStore.OpenAsync(path, cancelled.Token));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_AcceptsTuningOptions()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var store = new LatticeCodeGraphStore(
                path,
                new LatticeDbStoreOptions { CacheSizeMb = 32, EnableWal = true });
            await store.UpsertRepositoryAsync(new(new CodeRepositoryId("repo:options")));
            Assert.True(store.CheckHealth().IsHealthy);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Host_UseLatticeStoreBuildsHealthyHost()
    {
        var path = TemporaryDatabasePath();
        try
        {
            await using var host = new HetuHostBuilder()
                .UseLatticeStore(path)
                .Build();
            var health = await host.CheckHealthAsync();
            Assert.True(health.IsReady);
            Assert.Equal("lattice", health.Store.StoreName);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void Store_RejectsSecondOwnerOfSameFile()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var first = new LatticeCodeGraphStore(path);
            Assert.Throws<LatticeException>(() => new LatticeCodeGraphStore(path));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ReportsUnhealthyAfterDispose()
    {
        var path = TemporaryDatabasePath();
        var store = new LatticeCodeGraphStore(path);
        try
        {
            Assert.True(store.CheckHealth().IsHealthy);
        }
        finally
        {
            store.Dispose();
            DeleteDatabase(path);
        }

        var health = await ((ICodeGraphStoreHealthCheck)store).CheckHealthAsync();
        Assert.Equal(CodeGraphStoreHealthStatus.Unhealthy, health.Status);
        Assert.Equal("lattice", health.StoreName);
        Assert.NotNull(health.Detail);
    }

    [Fact]
    public async Task Store_IsolatesMultipleRepositories()
    {
        var path = TemporaryDatabasePath();
        var firstRepository = new CodeRepositoryId("repo:first");
        var secondRepository = new CodeRepositoryId("repo:second");
        var started = DateTimeOffset.UtcNow;
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                foreach (var repository in new[] { firstRepository, secondRepository })
                {
                    var runId = new CodeIndexRunId($"run:{repository.Value}");
                    await first.UpsertRepositoryAsync(new(repository));
                    await first.StoreIndexRunAsync(new(repository, runId, started));
                    await first.CompleteIndexRunAsync(
                        new(repository, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1)),
                        new(repository, runId, []));
                }
            }

            using var reopened = new LatticeCodeGraphStore(path);
            foreach (var repository in new[] { firstRepository, secondRepository })
            {
                var state = await reopened.GetLatestIndexStateAsync(repository);
                Assert.NotNull(state);
                Assert.Equal($"run:{repository.Value}", state.IndexRunId.Value);
                var publication = await reopened.GetLatestPublicationAsync(repository);
                Assert.NotNull(publication);
                Assert.Equal(state.IndexRunId, publication.IndexRunId);
            }
            Assert.Equal(2, reopened.CheckHealth().RepositoryCount);
            Assert.Equal(2, reopened.CheckHealth().RunCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void DeleteDatabase(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }

        foreach (var suffix in new[] { "-wal", "-journal", ".wal" })
        {
            try
            {
                if (File.Exists(path + suffix))
                    File.Delete(path + suffix);
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class Fixture(string path) : ICodeGraphStoreFixture
    {
        public LatticeCodeGraphStore? Store { get; private set; }

        public ICodeGraphStore CreateStore() => Store = new(path);
    }

    private sealed class InjectedPersistenceException : Exception;
}
