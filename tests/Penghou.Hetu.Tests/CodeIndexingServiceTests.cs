using System.Runtime.CompilerServices;
using System.Text;

namespace Penghou.Hetu.Tests;

public sealed class CodeIndexingServiceTests
{
    [Fact]
    public async Task IndexAsync_CompletesRunPublishesStateAndDeletesObsoletePluginUnit()
    {
        var files = new Dictionary<string, string> { ["src/Example.cs"] = "class Example {}" };
        var provider = new MemoryProvider(files);
        var plugin = new LifecyclePlugin();
        var store = new InMemoryCodeGraphStore();
        var service = Service(provider, plugin, store);
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");

        var first = await service.IndexAsync(descriptor, new("run:first"));

        Assert.Equal(CodeIndexRunStatus.Completed, first.Diagnostics.Status);
        Assert.Equal(descriptor.Id, first.Publication.RepositoryId);
        Assert.Equal(new CodeIndexRunId("run:first"), first.Publication.IndexRunId);
        Assert.Equal("snapshot:test", first.Publication.SnapshotIdentity);
        Assert.True(first.Publication.IsConsistentSnapshot);
        Assert.Equal(first.PublishedState.IndexIdentity, first.Publication.IndexIdentity);
        Assert.Equal(64, first.Publication.IndexIdentity.Value.Length);
        Assert.Equal(1, first.Diagnostics.FilesNew);
        Assert.Equal(1, first.Diagnostics.IndexUnitsCompleted);
        Assert.Equal(2, first.Diagnostics.UnresolvedRelationships);
        var pluginDiagnostics = Assert.Single(first.Diagnostics.Plugins);
        Assert.Equal(plugin.Id, pluginDiagnostics.PluginId);
        Assert.Equal(1, pluginDiagnostics.SourcesExamined);
        Assert.Equal(1, pluginDiagnostics.SourcesContributingFacts);
        Assert.Equal(2, pluginDiagnostics.UnresolvedRelationships);
        Assert.Contains("test.unresolved", pluginDiagnostics.WarningCodes);
        Assert.True(pluginDiagnostics.Duration >= TimeSpan.Zero);
        Assert.NotNull(await store.GetNodeAsync(descriptor.Id, new("node:example")));
        Assert.Equal("run:first", (await store.GetLatestIndexStateAsync(descriptor.Id))!.IndexRunId.Value);

        files.Clear();
        var second = await service.IndexAsync(descriptor, new("run:second"));

        Assert.Equal(1, second.Diagnostics.FilesDeleted);
        Assert.Equal(1, second.Diagnostics.IndexUnitsDeleted);
        Assert.Null(await store.GetNodeAsync(descriptor.Id, new("node:example")));
        Assert.Empty((await store.GetLatestIndexStateAsync(descriptor.Id))!.Sources);
        Assert.Equal(CodeGraphSourceChangeKind.Deleted, plugin.LastChanges.Single().Kind);
        Assert.Equal(second.PublishedState.IndexIdentity, second.Publication.IndexIdentity);
    }

    [Fact]
    public async Task IndexAsync_FailedRunRetainsLastSuccessfulSourceState()
    {
        var files = new Dictionary<string, string> { ["src/Example.cs"] = "one" };
        var provider = new MemoryProvider(files);
        var plugin = new LifecyclePlugin();
        var store = new InMemoryCodeGraphStore();
        var service = Service(provider, plugin, store);
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");
        await service.IndexAsync(descriptor, new("run:first"));
        var firstState = await store.GetLatestIndexStateAsync(descriptor.Id);
        files["src/Example.cs"] = "two";
        plugin.Fail = true;
        CodeIndexingDiagnostics? diagnostics = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.IndexAsync(
                descriptor,
                new("run:failed"),
                diagnostics: value => diagnostics = value));

        var failed = await store.GetIndexRunAsync(descriptor.Id, new("run:failed"));
        Assert.Equal(CodeIndexRunStatus.Failed, failed!.Status);
        Assert.Equal(firstState!.IndexRunId, (await store.GetLatestIndexStateAsync(descriptor.Id))!.IndexRunId);
        Assert.NotNull(diagnostics);
        Assert.Equal(CodePluginIndexingStatus.Failed, Assert.Single(diagnostics.Plugins).Status);
        Assert.Contains("plugin.failed", diagnostics.WarningCodes);
    }

    [Fact]
    public async Task IndexAsync_DiagnosticsCallbackCannotBreakSuccessfulIndexing()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "content" });
        var store = new InMemoryCodeGraphStore();

        var result = await Service(provider, new LifecyclePlugin(), store).IndexAsync(
            new CodeRepositoryDescriptor(new("repo:test"), "memory://test"),
            new CodeIndexRunId("run:one"),
            diagnostics: _ => throw new InvalidOperationException("observer failure"));

        Assert.Equal(CodeIndexRunStatus.Completed, result.Diagnostics.Status);
    }

    [Fact]
    public async Task IndexAsync_CancelledExtractionRecordsCancelledRun()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "content" });
        var plugin = new LifecyclePlugin { Cancel = true };
        var store = new InMemoryCodeGraphStore();
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Service(provider, plugin, store).IndexAsync(descriptor, new("run:cancelled")));

        var run = await store.GetIndexRunAsync(descriptor.Id, new("run:cancelled"));
        Assert.Equal(CodeIndexRunStatus.Cancelled, run!.Status);
        Assert.Null(await store.GetLatestIndexStateAsync(descriptor.Id));
    }

    [Fact]
    public async Task IndexAsync_SkipsPluginWhenEverySourceIsUnchanged()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "content" });
        var plugin = new LifecyclePlugin();
        var store = new InMemoryCodeGraphStore();
        var service = Service(provider, plugin, store);
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");
        var first = await service.IndexAsync(descriptor, new("run:first"));

        var second = await service.IndexAsync(descriptor, new("run:second"));

        Assert.Equal(1, plugin.ExecutionCount);
        Assert.Equal(0, second.Diagnostics.PluginsExecuted);
        Assert.Equal(Encoding.UTF8.GetByteCount("content"), second.Diagnostics.SourceBytesRead);
        Assert.Equal(3, provider.OpenCount);
        Assert.Equal(1, second.Diagnostics.FilesUnchanged);
        Assert.Equal("run:second", second.Publication.IndexRunId.Value);
        Assert.Equal(first.Publication.IndexIdentity, second.Publication.IndexIdentity);
        Assert.Equal(
            (await store.GetLatestIndexStateAsync(descriptor.Id))!.IndexIdentity,
            second.Publication.IndexIdentity);
    }

    [Fact]
    public async Task IndexAsync_RejectsBatchOutsideExecutingPluginScope()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "content" });
        var plugin = new LifecyclePlugin { EmitForeignOrigin = true };
        var store = new InMemoryCodeGraphStore();
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");

        var exception = await Assert.ThrowsAsync<CodeGraphBatchRejectedException>(async () =>
            await Service(provider, plugin, store).IndexAsync(descriptor, new("run:foreign")));

        Assert.Contains(exception.Errors, error => error.Code == "lifecycle.batch.ownership-mismatch");
        Assert.Equal(
            CodeIndexRunStatus.Failed,
            (await store.GetIndexRunAsync(descriptor.Id, new("run:foreign")))!.Status);
    }

    [Fact]
    public async Task IndexAsync_DetectsSourceChangedAfterPlanning()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "first" })
        {
            ChangeContentOnSecondOpen = true
        };
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");

        await Assert.ThrowsAsync<CodeSourceChangedDuringIndexingException>(async () =>
            await Service(provider, new LifecyclePlugin(), new InMemoryCodeGraphStore())
                .IndexAsync(descriptor, new("run:changed")));
    }

    [Fact]
    public async Task IndexAsync_EnforcesPerSourceByteLimit()
    {
        var provider = new MemoryProvider(
            new Dictionary<string, string> { ["src/Example.cs"] = "too-large" });
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");
        var options = new CodeIndexingOptions(maxSourceBytes: 4, maxTotalSourceBytes: 8);

        var exception = await Assert.ThrowsAsync<CodeSourceSizeLimitException>(async () =>
            await Service(provider, new LifecyclePlugin(), new InMemoryCodeGraphStore())
                .IndexAsync(descriptor, new("run:large"), options));

        Assert.False(exception.IsTotalLimit);
        Assert.Equal(4, exception.MaximumBytes);
    }

    [Fact]
    public async Task IndexAsync_EnforcesTotalHashByteLimit()
    {
        var provider = new MemoryProvider(new Dictionary<string, string>
        {
            ["src/One.cs"] = "123456",
            ["src/Two.cs"] = "abcdef"
        });
        var descriptor = new CodeRepositoryDescriptor(new("repo:test"), "memory://test");
        var options = new CodeIndexingOptions(maxSourceBytes: 8, maxTotalSourceBytes: 10);

        var exception = await Assert.ThrowsAsync<CodeSourceSizeLimitException>(async () =>
            await Service(provider, new LifecyclePlugin(), new InMemoryCodeGraphStore())
                .IndexAsync(descriptor, new("run:large-total"), options));

        Assert.True(exception.IsTotalLimit);
    }

    [Fact]
    public async Task IndexAsync_ExecutesPluginsInParallelWithinConfiguredBound()
    {
        var provider = new MemoryProvider(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "csharp",
            ["src/Example.ts"] = "typescript"
        });
        var probe = new ConcurrencyProbe(expectedConcurrency: 2);
        ICodeGraphPlugin[] plugins =
        [
            new ConcurrentPlugin("plugin:csharp", ".cs", probe),
            new ConcurrentPlugin("plugin:typescript", ".ts", probe)
        ];
        var service = new CodeIndexingService(
            new CodeRepositoryProviderRegistry([provider]),
            new CodeGraphPluginRegistry(plugins),
            new InMemoryCodeGraphStore());

        var result = await service.IndexAsync(
            new CodeRepositoryDescriptor(new("repo:test"), "memory://test"),
            new CodeIndexRunId("run:parallel"),
            new CodeIndexingOptions(maxConcurrentPlugins: 2));

        Assert.Equal(2, probe.MaximumObserved);
        Assert.Equal(2, result.Diagnostics.PluginsExecuted);
    }

    private static CodeIndexingService Service(
        MemoryProvider provider,
        LifecyclePlugin plugin,
        ICodeGraphStore store) =>
        new(new CodeRepositoryProviderRegistry([provider]), new([plugin]), store);

    private sealed class LifecyclePlugin : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new("plugin:csharp");
        public string Version => "1.0.0";
        public string Language => "csharp";
        public IReadOnlyCollection<string> FileExtensions => [".cs"];
        public CodeGraphCapabilities Capabilities => CodeGraphCapabilities.Syntax;
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public bool EmitForeignOrigin { get; set; }
        public int ExecutionCount { get; private set; }
        public IReadOnlyList<CodeGraphSourceChange> LastChanges { get; private set; } = [];

        public bool CanHandle(string path) => path.EndsWith(".cs", StringComparison.Ordinal);

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default)
        {
            LastChanges = context.Changes;
            return new(new Session(context, this));
        }

        private sealed class Session(
            CodeGraphPluginContext context,
            LifecyclePlugin plugin) : ICodeGraphExtractionSession
        {
            public async ValueTask<CodeGraphExtractionResult> ExtractAsync(
                ICodeGraphSink sink,
                CancellationToken cancellationToken = default)
            {
                plugin.ExecutionCount++;
                if (plugin.Fail)
                    throw new InvalidOperationException("plugin failure");
                if (plugin.Cancel)
                    throw new OperationCanceledException("plugin cancellation");
                if (context.Sources.Count == 0)
                    return new([new CodeIndexUnitId("unit:example")]);

                var origin = new CodeFactOrigin(
                    context.RepositoryId,
                    plugin.EmitForeignOrigin ? new CodePluginId("plugin:foreign") : plugin.Id,
                    plugin.Version,
                    context.IndexRunId,
                    new CodeIndexUnitId("unit:example"));
                await sink.WriteBatchAsync(
                    new CodeGraphBatch(
                        origin,
                        nodes:
                        [
                            new CodeGraphNode(
                                new CodeNodeId("node:example"),
                                CodeNodeKinds.Type,
                                "Example")
                        ],
                        completesIndexUnit: true),
                    cancellationToken);
                return new(
                    sourcesExamined: context.Sources.Count,
                    sourcesContributingFacts: context.Sources.Count,
                    unresolvedRelationships: 2,
                    warningCodes: ["test.unresolved"]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryProvider(Dictionary<string, string> files) : ICodeRepositoryProvider
    {
        private int _openCount;

        public string Name => "memory";
        public bool ChangeContentOnSecondOpen { get; init; }
        public int OpenCount => _openCount;
        public bool CanOpen(CodeRepositoryDescriptor repository) =>
            repository.Location.StartsWith("memory://", StringComparison.Ordinal);
        public ValueTask<ICodeRepositorySource> OpenAsync(
            CodeRepositoryDescriptor repository,
            CancellationToken cancellationToken = default) =>
            new(new Source(repository.Id, this, files));

        private Stream Open(CodeRepositoryEntry entry)
        {
            var content = files[entry.Path];
            var openCount = Interlocked.Increment(ref _openCount);
            if (ChangeContentOnSecondOpen && openCount == 2)
                content += "-changed";
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }

        private sealed class Source(
            CodeRepositoryId repositoryId,
            MemoryProvider provider,
            Dictionary<string, string> files) : ICodeRepositorySource
        {
            public CodeRepositoryId RepositoryId { get; } = repositoryId;
            public string ProviderName => "memory";
            public string? SnapshotIdentity => "snapshot:test";
            public bool IsConsistentSnapshot => true;

            public async IAsyncEnumerable<CodeRepositoryEntry> EnumerateAsync(
                CodeRepositoryEnumerationOptions options,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new(path, Encoding.UTF8.GetByteCount(content));
                    await Task.Yield();
                }
            }

            public ValueTask<Stream> OpenReadAsync(
                CodeRepositoryEntry entry,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(provider.Open(entry));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrentPlugin(
        string id,
        string extension,
        ConcurrencyProbe probe) : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new(id);
        public string Version => "1.0.0";
        public string Language => id;
        public IReadOnlyCollection<string> FileExtensions => [extension];
        public CodeGraphCapabilities Capabilities => CodeGraphCapabilities.Syntax;
        public bool CanHandle(string path) => path.EndsWith(extension, StringComparison.Ordinal);

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) =>
            new(new Session(context, this, probe));

        private sealed class Session(
            CodeGraphPluginContext context,
            ConcurrentPlugin plugin,
            ConcurrencyProbe probe) : ICodeGraphExtractionSession
        {
            public async ValueTask<CodeGraphExtractionResult> ExtractAsync(
                ICodeGraphSink sink,
                CancellationToken cancellationToken = default)
            {
                await probe.EnterAsync(cancellationToken);
                try
                {
                    await sink.WriteBatchAsync(
                        new CodeGraphBatch(
                            new CodeFactOrigin(
                                context.RepositoryId,
                                plugin.Id,
                                plugin.Version,
                                context.IndexRunId,
                                new CodeIndexUnitId($"unit:{plugin.Id.Value}")),
                            completesIndexUnit: true),
                        cancellationToken);
                    return new();
                }
                finally
                {
                    probe.Exit();
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrencyProbe(int expectedConcurrency)
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maximumObserved;

        public int MaximumObserved => _maximumObserved;

        public async ValueTask EnterAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = _maximumObserved;
                if (active <= observed)
                    break;
            }
            while (Interlocked.CompareExchange(ref _maximumObserved, active, observed) != observed);

            if (active >= expectedConcurrency)
                _reached.TrySetResult();
            await _reached.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }
}
