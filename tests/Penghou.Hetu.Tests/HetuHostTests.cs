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
}
