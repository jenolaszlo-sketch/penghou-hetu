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
        Assert.Equal(1, first.Diagnostics.FilesNew);
        Assert.Equal(1, first.Diagnostics.IndexUnitsCompleted);
        Assert.NotNull(await store.GetNodeAsync(descriptor.Id, new("node:example")));
        Assert.Equal("run:first", (await store.GetLatestIndexStateAsync(descriptor.Id))!.IndexRunId.Value);

        files.Clear();
        var second = await service.IndexAsync(descriptor, new("run:second"));

        Assert.Equal(1, second.Diagnostics.FilesDeleted);
        Assert.Equal(1, second.Diagnostics.IndexUnitsDeleted);
        Assert.Null(await store.GetNodeAsync(descriptor.Id, new("node:example")));
        Assert.Empty((await store.GetLatestIndexStateAsync(descriptor.Id))!.Sources);
        Assert.Equal(CodeGraphSourceChangeKind.Deleted, plugin.LastChanges.Single().Kind);
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

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.IndexAsync(descriptor, new("run:failed")));

        var failed = await store.GetIndexRunAsync(descriptor.Id, new("run:failed"));
        Assert.Equal(CodeIndexRunStatus.Failed, failed!.Status);
        Assert.Equal(firstState!.IndexRunId, (await store.GetLatestIndexStateAsync(descriptor.Id))!.IndexRunId);
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
                if (plugin.Fail)
                    throw new InvalidOperationException("plugin failure");
                if (plugin.Cancel)
                    throw new OperationCanceledException("plugin cancellation");
                if (context.Sources.Count == 0)
                    return new([new CodeIndexUnitId("unit:example")]);

                var origin = new CodeFactOrigin(
                    context.RepositoryId,
                    plugin.Id,
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
                return new();
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryProvider(Dictionary<string, string> files) : ICodeRepositoryProvider
    {
        public string Name => "memory";
        public bool CanOpen(CodeRepositoryDescriptor repository) =>
            repository.Location.StartsWith("memory://", StringComparison.Ordinal);
        public ValueTask<ICodeRepositorySource> OpenAsync(
            CodeRepositoryDescriptor repository,
            CancellationToken cancellationToken = default) =>
            new(new Source(repository.Id, files));

        private sealed class Source(
            CodeRepositoryId repositoryId,
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
                Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(files[entry.Path]));
                return new(stream);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
