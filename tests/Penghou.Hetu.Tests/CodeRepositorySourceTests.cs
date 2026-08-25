using System.Text;
using System.Runtime.CompilerServices;

namespace Penghou.Hetu.Tests;

public sealed class CodeRepositorySourceTests
{
    [Fact]
    public async Task FileSystemSource_EnumeratesLazilyWithDefaultExclusions()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "README.md"),
                "readme");
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "Example.cs"),
                "class Example {}");
            await File.WriteAllTextAsync(
                Path.Combine(root, "obj", "Generated.cs"),
                "class Generated {}");
            var provider = new FileSystemCodeRepositoryProvider();
            await using var source = await provider.OpenAsync(Descriptor(root));
            var discoveryEvents = new List<CodeRepositoryDiscoveryEventKind>();

            var entries = await EnumerateAsync(
                source,
                new CodeRepositoryEnumerationOptions(observer: discoveryEvents.Add));

            Assert.Equal(
                ["README.md", "src/Example.cs"],
                entries.Select(entry => entry.Path));
            Assert.False(source.IsConsistentSnapshot);
            Assert.Null(source.SnapshotIdentity);
            Assert.Contains(
                CodeRepositoryDiscoveryEventKind.DirectoryExcluded,
                discoveryEvents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemSource_OpensSelectedEntryLazily()
    {
        var root = CreateRepository();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Example.cs"),
                "class Example {}");
            var provider = new FileSystemCodeRepositoryProvider();
            await using var source = await provider.OpenAsync(Descriptor(root));
            var entry = Assert.Single(await EnumerateAsync(
                source,
                new CodeRepositoryEnumerationOptions()));

            await using var stream = await source.OpenReadAsync(entry);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            Assert.Equal("class Example {}", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemSource_EnforcesEntryLimit()
    {
        var root = CreateRepository();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "One.cs"), "one");
            await File.WriteAllTextAsync(Path.Combine(root, "Two.cs"), "two");
            var provider = new FileSystemCodeRepositoryProvider();
            await using var source = await provider.OpenAsync(Descriptor(root));

            await Assert.ThrowsAsync<CodeRepositoryEnumerationLimitException>(
                async () => await EnumerateAsync(
                    source,
                    new CodeRepositoryEnumerationOptions(maxEntries: 1)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RegistersFileSystemProviderByDefault()
    {
        var root = CreateRepository();
        try
        {
            var registry = new HetuBuilder().BuildRepositoryProviderRegistry();

            await using var source = await registry.OpenAsync(Descriptor(root));

            Assert.Equal("filesystem", source.ProviderName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_AllowsCustomProviderRegistration()
    {
        var registry = new HetuBuilder()
            .AddRepositoryProvider(new MemoryProvider())
            .BuildRepositoryProviderRegistry();
        var descriptor = new CodeRepositoryDescriptor(
            new CodeRepositoryId("repo:memory"),
            "memory://sample");

        await using var source = await registry.OpenAsync(descriptor);
        var entry = Assert.Single(await EnumerateAsync(
            source,
            new CodeRepositoryEnumerationOptions()));

        Assert.Equal("src/Memory.cs", entry.Path);
    }

    [Fact]
    public void Registry_ReportsProviderAmbiguityRatherThanGuessing()
    {
        var registry = new HetuBuilder()
            .ClearRepositoryProviders()
            .AddRepositoryProvider(new MemoryProvider("memory:first"))
            .AddRepositoryProvider(new MemoryProvider("memory:second"))
            .BuildRepositoryProviderRegistry();
        var descriptor = new CodeRepositoryDescriptor(
            new CodeRepositoryId("repo:memory"),
            "memory://sample");

        var exception = Assert.Throws<CodeRepositoryProviderSelectionException>(
            () => registry.Resolve(descriptor));

        Assert.Equal(
            ["memory:first", "memory:second"],
            exception.CandidateNames);
    }

    [Fact]
    public async Task Builder_CanReplaceDefaultProvidersEntirely()
    {
        var registry = new HetuBuilder()
            .ClearRepositoryProviders()
            .AddRepositoryProvider(new MemoryProvider())
            .BuildRepositoryProviderRegistry();
        var descriptor = new CodeRepositoryDescriptor(
            new CodeRepositoryId("repo:unsupported"),
            "unsupported://sample");

        await Assert.ThrowsAsync<CodeRepositoryProviderNotFoundException>(
            async () => await registry.OpenAsync(descriptor));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../outside.cs")]
    [InlineData("/absolute.cs")]
    public void Entry_RejectsNonNormalizedOrEscapingPath(string path)
    {
        Assert.Throws<ArgumentException>(() => new CodeRepositoryEntry(path));
    }

    private static async Task<IReadOnlyList<CodeRepositoryEntry>> EnumerateAsync(
        ICodeRepositorySource source,
        CodeRepositoryEnumerationOptions options)
    {
        var entries = new List<CodeRepositoryEntry>();
        await foreach (var entry in source.EnumerateAsync(options))
            entries.Add(entry);
        return entries;
    }

    private static CodeRepositoryDescriptor Descriptor(string root) =>
        new(new CodeRepositoryId("repo:test"), root);

    private static string CreateRepository()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "hetu-repository-source-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class MemoryProvider(string name = "memory")
        : ICodeRepositoryProvider
    {
        public string Name => name;

        public bool CanOpen(CodeRepositoryDescriptor repository) =>
            repository.Location.StartsWith(
                "memory://",
                StringComparison.Ordinal);

        public ValueTask<ICodeRepositorySource> OpenAsync(
            CodeRepositoryDescriptor repository,
            CancellationToken cancellationToken = default) =>
            new(new MemorySource(repository.Id, Name));
    }

    private sealed class MemorySource(
        CodeRepositoryId repositoryId,
        string providerName) : ICodeRepositorySource
    {
        public CodeRepositoryId RepositoryId => repositoryId;
        public string ProviderName => providerName;
        public string? SnapshotIdentity => "memory-v1";
        public bool IsConsistentSnapshot => true;

        public async IAsyncEnumerable<CodeRepositoryEntry> EnumerateAsync(
            CodeRepositoryEnumerationOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new CodeRepositoryEntry(
                "src/Memory.cs",
                contentHash: "sha256:test");
        }

        public ValueTask<Stream> OpenReadAsync(
            CodeRepositoryEntry entry,
            CancellationToken cancellationToken = default) =>
            new(new MemoryStream(Encoding.UTF8.GetBytes("class Memory {}")));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
