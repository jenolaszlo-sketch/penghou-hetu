using Penghou.Hetu.Testing;
using System.Runtime.InteropServices;
using LadybugDB;

namespace Penghou.Hetu.Ladybug.Tests;

public sealed class LadybugCodeGraphStoreTests
{
    [Fact]
    public async Task Store_PassesProviderConformanceSuite()
    {
        var path = TemporaryDatabasePath();
        if (!NativeRuntimeIsAvailable(path))
            return;
        var fixture = new Fixture(path);
        try
        {
            var report = await CodeGraphStoreConformanceSuite.VerifyAsync(fixture);

            Assert.Contains("bounded-deterministic-traversal", report.PassedChecks);
            Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
            Assert.True(fixture.Store!.CheckHealth().IsHealthy);
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
        if (!NativeRuntimeIsAvailable(path))
            return;
        var repositoryId = new CodeRepositoryId("repo:durable");
        try
        {
            using (var first = new LadybugCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(
                    repositoryId,
                    "Durable repository",
                    "repo://durable"));
            }

            using var reopened = new LadybugCodeGraphStore(path);
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
        if (!NativeRuntimeIsAvailable(path))
            return;
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
            using (var first = new LadybugCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
                await first.ReplaceIndexUnitAsync(new(
                    new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:durable")),
                    [node]));
                await first.CompleteIndexRunAsync(
                    new(repositoryId, runId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [pluginId]),
                    new(repositoryId, runId, [new CodeSourceManifest(pluginId, "1.0.0", "src/Durable.cs", "sha256:durable")]));
            }

            using var reopened = new LadybugCodeGraphStore(path);

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
    public void Store_RejectsIncompatibleSchemaVersion()
    {
        var path = TemporaryDatabasePath();
        if (!NativeRuntimeIsAvailable(path))
            return;
        try
        {
            using (var database = new Database(path))
            using (var connection = new Connection(database))
            {
                connection.Query("MATCH (s:HetuMetadata) SET s.schemaVersion = 999").Dispose();
            }

            var exception = Assert.Throws<LadybugCodeGraphSchemaException>(
                () => new LadybugCodeGraphStore(path));
            Assert.Equal(999, exception.ActualVersion);
            Assert.Equal(LadybugCodeGraphStore.CurrentSchemaVersion, exception.ExpectedVersion);
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
        if (!NativeRuntimeIsAvailable(path))
            return;
        try
        {
            using (var first = new LadybugCodeGraphStore(path))
                await first.UpsertRepositoryAsync(new(new CodeRepositoryId("repo:corrupt")));
            using (var database = new Database(path))
            using (var connection = new Connection(database))
            {
                connection.Query("MATCH (s:HetuRepository) SET s.payload = 'not-base64'").Dispose();
            }

            Assert.Throws<FormatException>(() => new LadybugCodeGraphStore(path));
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
        if (!NativeRuntimeIsAvailable(path))
            return;
        var repositoryId = new CodeRepositoryId("repo:rollback");
        var runId = new CodeIndexRunId("run:rollback");
        var pluginId = new CodePluginId("plugin:rollback");
        var started = DateTimeOffset.UtcNow;
        var prior = new CodeGraphNode(new("node:prior"), CodeNodeKinds.Type, "Prior");
        var replacement = new CodeGraphNode(new("node:replacement"), CodeNodeKinds.Type, "Replacement");
        try
        {
            using (var first = new LadybugCodeGraphStore(path))
            {
                await first.UpsertRepositoryAsync(new(repositoryId));
                await first.StoreIndexRunAsync(new(repositoryId, runId, started, plugins: [pluginId]));
                await first.ReplaceIndexUnitAsync(Unit([prior]));
            }
            using (var interrupted = new LadybugCodeGraphStore(
                       path,
                       point =>
                       {
                           if (point == "before-commit")
                               throw new InjectedPersistenceException();
                       }))
            {
                await Assert.ThrowsAsync<InjectedPersistenceException>(async () =>
                    await interrupted.ReplaceIndexUnitAsync(Unit([replacement])));
            }

            using var reopened = new LadybugCodeGraphStore(path);
            Assert.NotNull(await reopened.GetNodeAsync(repositoryId, prior.Id));
            Assert.Null(await reopened.GetNodeAsync(repositoryId, replacement.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }

        CodeIndexUnitReplacement Unit(IReadOnlyList<CodeGraphNode> nodes) => new(
            new CodeFactOrigin(repositoryId, pluginId, "1.0.0", runId, new("unit:rollback")),
            nodes);
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hetu-ladybug-{Guid.NewGuid():N}");

    private static bool NativeRuntimeIsAvailable(string path)
    {
        LoadWindowsOpenSslDependency("libcrypto-3-x64.dll");
        LoadWindowsOpenSslDependency("libssl-3-x64.dll");
        try
        {
            using var store = new LadybugCodeGraphStore(path);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void LoadWindowsOpenSslDependency(string fileName)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "mingw64",
            "bin",
            fileName);
        if (File.Exists(candidate))
            NativeLibrary.Load(candidate);
    }

    private static void DeleteDatabase(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class Fixture(string path) : ICodeGraphStoreFixture
    {
        public LadybugCodeGraphStore? Store { get; private set; }

        public ICodeGraphStore CreateStore() => Store = new(path);
    }

    private sealed class InjectedPersistenceException : Exception;
}
