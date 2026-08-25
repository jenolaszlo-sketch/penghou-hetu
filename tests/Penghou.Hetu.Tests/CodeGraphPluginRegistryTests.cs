namespace Penghou.Hetu.Tests;

public sealed class CodeGraphPluginRegistryTests
{
    [Fact]
    public void Registry_OrdersPluginsDeterministically()
    {
        var registry = new CodeGraphPluginRegistry(
        [
            new FakePlugin("plugin:z", "typescript", ".ts"),
            new FakePlugin("plugin:b", "csharp", ".csx"),
            new FakePlugin("plugin:a", "csharp", ".cs")
        ]);

        Assert.Equal(
            ["plugin:a", "plugin:b", "plugin:z"],
            registry.Plugins.Select(plugin => plugin.Id.Value));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoPluginClaimsPath()
    {
        var registry = new CodeGraphPluginRegistry(
            [new FakePlugin("plugin:csharp", "csharp", ".cs")]);

        Assert.Null(registry.Resolve("README.md"));
    }

    [Fact]
    public void Resolve_ReportsAmbiguousClaimsRatherThanGuessing()
    {
        var registry = new CodeGraphPluginRegistry(
        [
            new FakePlugin("plugin:first", "csharp", ".cs"),
            new FakePlugin("plugin:second", "csharp", ".cs")
        ]);

        var exception = Assert.Throws<CodeGraphPluginSelectionException>(() =>
            registry.Resolve("Example.cs"));

        Assert.Equal(
            ["plugin:first", "plugin:second"],
            exception.CandidateIds.Select(id => id.Value));
    }

    [Fact]
    public void Registry_RejectsDuplicatePluginIdentity()
    {
        Assert.Throws<ArgumentException>(() => new CodeGraphPluginRegistry(
        [
            new FakePlugin("plugin:same", "csharp", ".cs"),
            new FakePlugin("plugin:same", "typescript", ".ts")
        ]));
    }

    private sealed class FakePlugin(
        string id,
        string language,
        string extension) : ICodeGraphPlugin
    {
        public CodePluginId Id { get; } = new(id);
        public string Version => "1.0.0";
        public string Language => language;
        public IReadOnlyCollection<string> FileExtensions => [extension];
        public CodeGraphCapabilities Capabilities => CodeGraphCapabilities.Syntax;

        public bool CanHandle(string path) =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
            CodeGraphPluginContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
