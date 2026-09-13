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
            Assert.Contains("superseded-run-completion-conflicts", report.PassedChecks);
            Assert.Contains("fresh-run-completes-after-conflict", report.PassedChecks);
            Assert.Contains("traversal-relationship-kind-filter", report.PassedChecks);
            Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
            Assert.Contains("latest-publication-round-trip", report.PassedChecks);
            Assert.True((await fixture.Store!.CheckHealthAsync()).IsHealthy);
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
            Assert.True((await reopened.CheckHealthAsync()).IsHealthy);
            Assert.Equal(1, (await reopened.CheckHealthAsync()).RepositoryCount);
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
            Assert.Equal(1, (await reopened.CheckHealthAsync()).IndexUnitCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task Store_ResumedRunConflictsAfterNewerPublication()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:ordering");
        var pluginId = new CodePluginId("plugin:ordering");
        var started = DateTimeOffset.UtcNow;
        var firstRun = new CodeIndexRunId("run:one");
        var staleRun = new CodeIndexRunId("run:stale");
        try
        {
            using (var first = new LatticeCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, firstRun, started, plugins: [pluginId]));
                await first.CompleteIndexRunAsync(
                    new(repositoryId, firstRun, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                    new(repositoryId, firstRun, []));
                await first.StoreIndexRunAsync(new(repositoryId, staleRun, started.AddSeconds(2), plugins: [pluginId]));
            }

            using var reopened = new LatticeCodeGraphStore(path);
            var nextRun = new CodeIndexRunId("run:two");
            await reopened.StoreIndexRunAsync(new(repositoryId, nextRun, started.AddSeconds(3), plugins: [pluginId]));
            await reopened.CompleteIndexRunAsync(
                new(repositoryId, nextRun, started.AddSeconds(3), CodeIndexRunStatus.Completed, started.AddSeconds(4), [pluginId]),
                new(repositoryId, nextRun, []));
            // The pre-restart baseline survived the reopen: the stale run
            // still conflicts instead of silently winning.
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CompleteIndexRunAsync(
                new(repositoryId, staleRun, started.AddSeconds(2), CodeIndexRunStatus.Completed, started.AddSeconds(5), [pluginId]),
                new(repositoryId, staleRun, [])).AsTask());
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

    [Fact]
    public async Task FailedCommitNeverPublishesProspectiveStateToReaders()
    {
        var path = TemporaryDatabasePath();
        using var enteredCommit = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        var fail = false;
        var repositoryId = new CodeRepositoryId("repo:visibility");
        try
        {
            using var store = new LatticeCodeGraphStore(path, null, point =>
            {
                if (!fail || point != "before-commit")
                    return;
                enteredCommit.Set();
                releaseCommit.Wait(TimeSpan.FromSeconds(15));
                throw new InjectedPersistenceException();
            });
            await store.UpsertRepositoryAsync(new(repositoryId, "old"));
            fail = true;
            var mutation = Task.Run(async () =>
                await store.UpsertRepositoryAsync(new(repositoryId, "uncommitted")));
            Assert.True(enteredCommit.Wait(TimeSpan.FromSeconds(15)));

            var read = store.GetRepositoryAsync(repositoryId).AsTask();
            await Task.Delay(100);
            Assert.False(read.IsCompleted);
            releaseCommit.Set();

            await Assert.ThrowsAsync<InjectedPersistenceException>(() => mutation);
            Assert.Equal("old", (await read)!.DisplayName);
        }
        finally
        {
            releaseCommit.Set();
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task FailedUnrelatedWritePreservesRunningRunBaseline()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:baseline-rollback");
        var pluginId = new CodePluginId("plugin:baseline-rollback");
        var firstRun = new CodeIndexRunId("run:first");
        var pendingRun = new CodeIndexRunId("run:pending");
        var started = DateTimeOffset.UtcNow;
        var fail = false;
        try
        {
            using var store = new LatticeCodeGraphStore(path, null, point =>
            {
                if (fail && point == "before-commit")
                    throw new InjectedPersistenceException();
            });
            await store.UpsertRepositoryAsync(new(repositoryId));
            await store.StoreIndexRunAsync(new(repositoryId, firstRun, started, plugins: [pluginId]));
            await store.CompleteIndexRunAsync(
                new(repositoryId, firstRun, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                new(repositoryId, firstRun, []));
            await store.StoreIndexRunAsync(new(repositoryId, pendingRun, started.AddSeconds(2), plugins: [pluginId]));

            fail = true;
            await Assert.ThrowsAsync<InjectedPersistenceException>(() =>
                store.UpsertRepositoryAsync(new(repositoryId, "failed update")).AsTask());
            fail = false;

            await store.CompleteIndexRunAsync(
                new(repositoryId, pendingRun, started.AddSeconds(2), CodeIndexRunStatus.Completed, started.AddSeconds(3), [pluginId]),
                new(repositoryId, pendingRun, []));
            Assert.Equal(pendingRun, (await store.GetLatestPublicationAsync(repositoryId))!.IndexRunId);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task IdempotentRunningRetryDoesNotRewriteOriginalBaseline()
    {
        var path = TemporaryDatabasePath();
        var repositoryId = new CodeRepositoryId("repo:idempotent-baseline");
        var pluginId = new CodePluginId("plugin:idempotent-baseline");
        var firstRun = new CodeIndexRunId("run:first");
        var staleRun = new CodeIndexRunId("run:stale");
        var secondRun = new CodeIndexRunId("run:second");
        var started = DateTimeOffset.UtcNow;
        var staleManifest = new CodeIndexRunManifest(
            repositoryId, staleRun, started.AddSeconds(2), plugins: [pluginId]);
        try
        {
            using (var store = new LatticeCodeGraphStore(path))
            {
                await store.UpsertRepositoryAsync(new(repositoryId));
                await PublishEmptyAsync(store, repositoryId, pluginId, firstRun, started);
                await store.StoreIndexRunAsync(staleManifest);
                await PublishEmptyAsync(store, repositoryId, pluginId, secondRun, started.AddSeconds(3));
                await store.StoreIndexRunAsync(staleManifest);
            }

            using var reopened = new LatticeCodeGraphStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CompleteIndexRunAsync(
                new(repositoryId, staleRun, started.AddSeconds(2), CodeIndexRunStatus.Completed, started.AddSeconds(5), [pluginId]),
                new(repositoryId, staleRun, [])).AsTask());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task DisposeWaitsForActiveMutationAndAllReadsRejectAfterward()
    {
        var path = TemporaryDatabasePath();
        using var enteredCommit = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        var block = false;
        var repositoryId = new CodeRepositoryId("repo:dispose");
        var store = new LatticeCodeGraphStore(path, null, point =>
        {
            if (!block || point != "before-commit")
                return;
            enteredCommit.Set();
            releaseCommit.Wait(TimeSpan.FromSeconds(15));
        });
        try
        {
            block = true;
            var mutation = Task.Run(async () =>
                await store.UpsertRepositoryAsync(new(repositoryId)));
            Assert.True(enteredCommit.Wait(TimeSpan.FromSeconds(15)));
            var disposal = Task.Run(store.Dispose);
            await Task.Delay(100);
            Assert.False(disposal.IsCompleted);
            releaseCommit.Set();
            await mutation;
            await disposal;

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                store.GetRepositoryAsync(repositoryId).AsTask());
        }
        finally
        {
            releaseCommit.Set();
            store.Dispose();
            DeleteDatabase(path);
        }
    }

    private static async Task PublishEmptyAsync(
        LatticeCodeGraphStore store,
        CodeRepositoryId repositoryId,
        CodePluginId pluginId,
        CodeIndexRunId runId,
        DateTimeOffset started)
    {
        await store.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
        await store.CompleteIndexRunAsync(
            new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
            new(repositoryId, runId, []));
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
            Assert.True((await store.CheckHealthAsync()).IsHealthy);

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
            Assert.True((await store.CheckHealthAsync()).IsHealthy);
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
            var failure = Assert.Throws<CodeGraphStoreException>(() => new LatticeCodeGraphStore(path));
            Assert.Equal("lattice", failure.StoreName);
            Assert.IsType<LatticeException>(failure.InnerException);
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
            Assert.True((await store.CheckHealthAsync()).IsHealthy);
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
            Assert.Equal(2, (await reopened.CheckHealthAsync()).RepositoryCount);
            Assert.Equal(2, (await reopened.CheckHealthAsync()).RunCount);
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

