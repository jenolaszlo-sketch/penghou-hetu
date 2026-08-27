namespace Penghou.Hetu.Tests;

public sealed class HetuHostTests
{
    [Fact]
    public async Task Build_WithInMemoryStore_CreatesQueryableHost()
    {
        await using var host = new HetuHostBuilder()
            .UseStore(() => new InMemoryCodeGraphStore())
            .AddPlugin(new TestPlugin())
            .Build();

        Assert.NotNull(host.Queries);
        Assert.NotNull(host.Indexing);
        Assert.NotNull(host.Reader);
    }

    [Fact]
    public async Task IndexRepositoryAsync_PublishesAndQueriesWork()
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hetu-host-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = System.IO.Path.Combine(tempDir, "Test.cs");
            await System.IO.File.WriteAllTextAsync(sourcePath,
                "namespace Example; public class Hello { public void World() { } }");

            await using var host = new HetuHostBuilder()
                .UseStore(() => new InMemoryCodeGraphStore())
                .AddPlugin(new TestPlugin())
                .Build();

            var runId = new CodeIndexRunId($"run:{Guid.NewGuid():N}");
            var result = await host.IndexRepositoryAsync(
                new CodeRepositoryDescriptor(
                    new CodeRepositoryId("repo:test"),
                    tempDir),
                runId);

            Assert.Equal(CodeIndexRunStatus.Completed, result.Diagnostics.Status);
            var lookups = await host.Queries.ResolveSymbolsAsync(
                result.Diagnostics.RepositoryId,
                ["Example.Hello"]);
            Assert.True(lookups.ContainsKey("Example.Hello"));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConfiguredIndexingOptions_AreDefaultsForRepositoryIndexing()
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"hetu-host-limit-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(tempDir, "Large.txt"),
                "larger-than-four-bytes");
            await using var host = new HetuHostBuilder()
                .UseStore(new InMemoryCodeGraphStore())
                .AddPlugin(new TestPlugin())
                .WithIndexingOptions(new CodeIndexingOptions(
                    maxSourceBytes: 4,
                    maxTotalSourceBytes: 8))
                .Build();

            var exception = await Assert.ThrowsAsync<CodeSourceSizeLimitException>(async () =>
                await host.IndexRepositoryAsync(
                    new CodeRepositoryDescriptor(new("repo:limited"), tempDir),
                    new CodeIndexRunId("run:limited")));

            Assert.False(exception.IsTotalLimit);
            Assert.Equal(4, exception.MaximumBytes);
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsStoreAndDeterministicComposition()
    {
        await using var host = new HetuHostBuilder()
            .UseStore(new InMemoryCodeGraphStore())
            .AddPlugin(new NamedPlugin("plugin:z"))
            .AddPlugin(new NamedPlugin("plugin:a"))
            .AddRepositoryProvider(new NamedProvider("z-provider"))
            .Build();

        var health = await host.CheckHealthAsync();

        Assert.True(health.IsReady);
        Assert.Equal(CodeGraphStoreHealthStatus.Healthy, health.Store.Status);
        Assert.Equal(["plugin:a", "plugin:z"],
            health.PluginIds.Select(id => id.Value));
        Assert.Equal(["filesystem", "z-provider"], health.RepositoryProviderNames);
    }

    private sealed class TestPlugin : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new("plugin:test");
        public string Version => "1.0.0";
        public string Language => "test";
        public IReadOnlyCollection<string> FileExtensions { get; } = [".txt"];
        public CodeGraphCapabilities Capabilities => new();

        public bool CanHandle(string path) =>
            path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) =>
            new(new Session(context));

        private sealed class Session(CodeGraphPluginContext context)
            : ICodeGraphExtractionSession
        {
            public async ValueTask<CodeGraphExtractionResult> ExtractAsync(
                ICodeGraphSink sink,
                CancellationToken cancellationToken = default)
            {
                var origin = new CodeFactOrigin(
                    context.RepositoryId, Id, "1.0.0", context.IndexRunId,
                    new("unit:test"));
                foreach (var source in context.Sources)
                {
                    await using var stream = await source.OpenReadAsync(cancellationToken);
                    using var reader = new System.IO.StreamReader(stream);
                    var text = await reader.ReadToEndAsync(cancellationToken);
                    var node = new CodeGraphNode(
                        new CodeNodeId($"node:{source.ContentHash}"),
                        CodeNodeKinds.File,
                        System.IO.Path.GetFileName(source.Path),
                        source.Path);
                    await sink.WriteBatchAsync(
                        new(origin, nodes: [node], completesIndexUnit: true),
                        cancellationToken);
                }

                return new(sourcesExamined: context.Sources.Count);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private CodePluginId Id => new("plugin:test");
        }
    }

    private sealed class NamedPlugin(string id) : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new(id);
        public string Version => "1.0.0";
        public string Language => id;
        public IReadOnlyCollection<string> FileExtensions => [".named"];
        public CodeGraphCapabilities Capabilities => CodeGraphCapabilities.None;
        public bool CanHandle(string path) => false;

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NamedProvider(string name) : ICodeRepositoryProvider
    {
        public string Name { get; } = name;
        public bool CanOpen(CodeRepositoryDescriptor repository) => false;

        public ValueTask<ICodeRepositorySource> OpenAsync(
            CodeRepositoryDescriptor repository,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
