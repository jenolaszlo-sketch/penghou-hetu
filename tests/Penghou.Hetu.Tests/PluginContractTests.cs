using Penghou.Hetu;
using System.Text;

namespace Penghou.Hetu.Tests;

public sealed class PluginContractTests
{
    [Fact]
    public async Task SyntaxAndProjectAwarePlugins_UseTheSameSessionContract()
    {
        var context = CreateContext();
        ICodeGraphPlugin syntaxPlugin = new FakePlugin(projectAware: false);
        ICodeGraphPlugin projectPlugin = new FakePlugin(projectAware: true);
        var syntaxSink = new RecordingSink();
        var projectSink = new RecordingSink();

        await using (var session = await syntaxPlugin.CreateSessionAsync(context))
            await session.ExtractAsync(syntaxSink);
        await using (var session = await projectPlugin.CreateSessionAsync(context))
            await session.ExtractAsync(projectSink);

        Assert.Equal(2, syntaxSink.Batches.Count);
        Assert.Single(projectSink.Batches);
        Assert.All(
            syntaxSink.Batches.Concat(projectSink.Batches),
            batch => Assert.NotNull(batch.Origin));
        Assert.True(projectSink.Batches.Single().CompletesIndexUnit);
    }

    [Fact]
    public void PluginContext_RejectsDuplicateSourcePaths()
    {
        var source = Source("src/One.cs", "one");

        Assert.Throws<ArgumentException>(() =>
            new CodeGraphPluginContext(
                new CodeRepositoryId("repo:test"),
                Path.GetTempPath(),
                new CodeIndexRunId("run:one"),
                [source, source]));
    }

    private static CodeGraphPluginContext CreateContext() =>
        new(
            new CodeRepositoryId("repo:test"),
            Path.GetTempPath(),
            new CodeIndexRunId("run:one"),
            [
                Source("src/One.cs", "class One {}"),
                Source("src/Two.cs", "class Two : One {}")
            ]);

    private static CodeGraphSource Source(string path, string content) =>
        new(
            path,
            $"hash:{path}",
            _ => new ValueTask<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(content))));

    private sealed class FakePlugin(bool projectAware) : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new("plugin:fake");
        public string Version => "1.0.0";
        public string Language => "fake";
        public IReadOnlyCollection<string> FileExtensions => [".cs"];
        public CodeGraphCapabilities Capabilities => projectAware
            ? CodeGraphCapabilities.Syntax | CodeGraphCapabilities.Symbols
            : CodeGraphCapabilities.Syntax;

        public bool CanHandle(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) =>
            new(new FakeSession(context, projectAware));
    }

    private sealed class FakeSession(
        CodeGraphPluginContext context,
        bool projectAware) : ICodeGraphExtractionSession
    {
        public async ValueTask ExtractAsync(
            ICodeGraphSink sink,
            CancellationToken cancellationToken = default)
        {
            var selected = projectAware
                ? [context.Sources]
                : context.Sources.Select(source =>
                    (IReadOnlyList<CodeGraphSource>)[source]);
            var groups = selected.ToArray();

            for (var index = 0; index < groups.Length; index++)
            {
                var group = groups[index];
                var origin = new CodeFactOrigin(
                    context.RepositoryId,
                    new CodePluginId("plugin:fake"),
                    "1.0.0",
                    context.IndexRunId,
                    new CodeIndexUnitId(
                        projectAware
                            ? "project:fake"
                            : $"file:{group.Single().Path}"),
                    projectAware ? null : group.Single().Path,
                    projectAware ? null : group.Single().ContentHash);
                await sink.WriteBatchAsync(
                    new CodeGraphBatch(
                        origin,
                        completesIndexUnit: true),
                    cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : ICodeGraphSink
    {
        public CodeGraphBatchLimits Limits { get; } = new();
        public List<CodeGraphBatch> Batches { get; } = [];

        public ValueTask WriteBatchAsync(
            CodeGraphBatch batch,
            CancellationToken cancellationToken = default)
        {
            Batches.Add(batch);
            return ValueTask.CompletedTask;
        }
    }
}
