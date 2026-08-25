using System.Runtime.CompilerServices;
using System.Text;

namespace Penghou.Hetu.Tests;

public sealed class CodeIndexPlannerTests
{
    [Fact]
    public async Task CreatePlan_ClassifiesNewChangedUnchangedAndDeletedUnits()
    {
        await using var source = new MemorySource(new Dictionary<string, string>
        {
            ["New.cs"] = "new",
            ["Changed.cs"] = "changed",
            ["Same.cs"] = "same",
            ["README.md"] = "ignored"
        });
        var plugin = new FakePlugin("1.0.0");
        var planner = new CodeIndexPlanner(new CodeGraphPluginRegistry([plugin]));
        var previous = new[]
        {
            Manifest(plugin, "Changed.cs", Hash("old")),
            Manifest(plugin, "Same.cs", Hash("same")),
            Manifest(plugin, "Deleted.cs", Hash("deleted"))
        };

        var plan = await planner.CreatePlanAsync(source, previous);

        Assert.Equal(
            [
                ("Changed.cs", CodeIndexPlanStatus.Changed),
                ("Deleted.cs", CodeIndexPlanStatus.Deleted),
                ("New.cs", CodeIndexPlanStatus.New),
                ("Same.cs", CodeIndexPlanStatus.Unchanged)
            ],
            plan.Items.Select(item => (item.Manifest.SourcePath, item.Status)));
        Assert.Null(plan.Items.Single(item => item.Status == CodeIndexPlanStatus.Deleted).Source);
        Assert.Equal(4, plan.RepositoryEntries);
        Assert.Equal(1, plan.UnsupportedEntries);
        Assert.True(plan.HashBytesRead > 0);
    }

    [Fact]
    public async Task CreatePlan_PluginVersionChangeInvalidatesUnchangedContent()
    {
        await using var source = new MemorySource(
            new Dictionary<string, string> { ["File.cs"] = "same" });
        var current = new FakePlugin("2.0.0");
        var old = new FakePlugin("1.0.0");

        var plan = await new CodeIndexPlanner(new([current])).CreatePlanAsync(
            source,
            [Manifest(old, "File.cs", Hash("same"))]);

        Assert.Equal(CodeIndexPlanStatus.Changed, Assert.Single(plan.Items).Status);
    }

    [Fact]
    public async Task CreatePlan_RepresentsMovedSourceAsDeletedAndNewWithoutGuessingIdentity()
    {
        await using var source = new MemorySource(
            new Dictionary<string, string> { ["New/Widget.cs"] = "same content" });
        var plugin = new FakePlugin("1.0.0");

        var plan = await new CodeIndexPlanner(new([plugin])).CreatePlanAsync(
            source,
            [Manifest(plugin, "Old/Widget.cs", Hash("same content"))]);

        Assert.Equal(
            [
                ("New/Widget.cs", CodeIndexPlanStatus.New),
                ("Old/Widget.cs", CodeIndexPlanStatus.Deleted)
            ],
            plan.Items.Select(item => (item.Manifest.SourcePath, item.Status)));
    }

    [Fact]
    public async Task CreatePlan_UsesProviderHashWithoutOpeningContent()
    {
        await using var source = new MemorySource(
            new Dictionary<string, string> { ["File.cs"] = "content" },
            exposeHashes: true,
            failOnOpen: true);

        var plan = await new CodeIndexPlanner(new([new FakePlugin("1.0.0")]))
            .CreatePlanAsync(source);

        Assert.Equal(Hash("content"), Assert.Single(plan.Items).Manifest.SourceHash);
    }

    [Fact]
    public async Task CreatePlan_RespectsExplicitPluginSelection()
    {
        await using var source = new MemorySource(new Dictionary<string, string>
        {
            ["File.cs"] = "csharp",
            ["File.ts"] = "typescript"
        });
        var csharp = new FakePlugin("1.0.0", "plugin:csharp", ".cs");
        var typescript = new FakePlugin("1.0.0", "plugin:typescript", ".ts");
        var options = new CodeIndexPlanningOptions(pluginIds: [typescript.Id]);

        var plan = await new CodeIndexPlanner(new([csharp, typescript]))
            .CreatePlanAsync(source, options: options);

        Assert.Equal("File.ts", Assert.Single(plan.Items).Manifest.SourcePath);
    }

    [Fact]
    public async Task CreatePlan_HonorsCancellationWhileHashing()
    {
        await using var source = new MemorySource(
            new Dictionary<string, string> { ["File.cs"] = "content" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new CodeIndexPlanner(new([new FakePlugin("1.0.0")]))
                .CreatePlanAsync(source, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CreatePlan_RejectsUnknownExplicitPluginSelection()
    {
        await using var source = new MemorySource(new Dictionary<string, string>());
        var options = new CodeIndexPlanningOptions(pluginIds: [new("plugin:missing")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new CodeIndexPlanner(new([new FakePlugin("1.0.0")]))
                .CreatePlanAsync(source, options: options));

        Assert.Contains("plugin:missing", exception.Message, StringComparison.Ordinal);
    }

    private static CodeSourceManifest Manifest(FakePlugin plugin, string path, string hash) =>
        new(plugin.Id, plugin.Version, path, hash);

    private static string Hash(string content) =>
        $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    private sealed class FakePlugin(
        string version,
        string id = "plugin:csharp",
        string extension = ".cs") : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new(id);
        public string Version => version;
        public string Language => "test";
        public IReadOnlyCollection<string> FileExtensions => [extension];
        public CodeGraphCapabilities Capabilities => CodeGraphCapabilities.Syntax;
        public bool CanHandle(string path) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemorySource(
        IReadOnlyDictionary<string, string> files,
        bool exposeHashes = false,
        bool failOnOpen = false) : ICodeRepositorySource
    {
        public CodeRepositoryId RepositoryId { get; } = new("repo:test");
        public string ProviderName => "memory";
        public string? SnapshotIdentity => "snapshot:1";
        public bool IsConsistentSnapshot => true;

        public async IAsyncEnumerable<CodeRepositoryEntry> EnumerateAsync(
            CodeRepositoryEnumerationOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(path, content.Length, contentHash: exposeHashes ? Hash(content) : null);
                await Task.Yield();
            }
        }

        public ValueTask<Stream> OpenReadAsync(
            CodeRepositoryEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (failOnOpen)
                throw new InvalidOperationException("Content should not be opened.");
            cancellationToken.ThrowIfCancellationRequested();
            Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(files[entry.Path]));
            return new(stream);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
