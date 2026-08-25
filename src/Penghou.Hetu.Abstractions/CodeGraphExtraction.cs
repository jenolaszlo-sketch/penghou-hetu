namespace Penghou.Hetu;

/// <summary>Repository-wide inputs available while creating an extraction session.</summary>
public sealed record CodeGraphPluginContext
{
    public CodeGraphPluginContext(
        CodeRepositoryId repositoryId,
        string? repositoryLocation,
        CodeIndexRunId indexRunId,
        IReadOnlyList<CodeGraphSource> sources,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        RepositoryId = repositoryId ??
            throw new ArgumentNullException(nameof(repositoryId));
        RepositoryLocation = string.IsNullOrWhiteSpace(repositoryLocation)
            ? null
            : repositoryLocation;
        IndexRunId = indexRunId ??
            throw new ArgumentNullException(nameof(indexRunId));
        Sources = sources?.ToArray() ??
            throw new ArgumentNullException(nameof(sources));
        if (Sources.Any(source => source is null))
        {
            throw new ArgumentException(
                "Plugin sources cannot contain null entries.",
                nameof(sources));
        }

        if (Sources
            .GroupBy(source => source.Path, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Plugin sources must have unique repository-relative paths.",
                nameof(sources));
        }

        Settings = CopySettings(settings);
    }

    public CodeRepositoryId RepositoryId { get; }
    /// <summary>
    /// Gets an optional provider-defined location hint. Plugins must use
    /// <see cref="Sources"/> to read content and must not assume this is a local path.
    /// </summary>
    public string? RepositoryLocation { get; }
    public CodeIndexRunId IndexRunId { get; }
    public IReadOnlyList<CodeGraphSource> Sources { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }

    private static IReadOnlyDictionary<string, string> CopySettings(
        IReadOnlyDictionary<string, string>? settings)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (settings is null)
            return copy;

        foreach (var (key, value) in settings)
        {
            copy.Add(
                ContractValue.Identifier(key, nameof(settings)),
                value ?? throw new ArgumentException(
                    "Plugin settings cannot contain null values.",
                    nameof(settings)));
        }

        return copy;
    }
}

/// <summary>Discovers normalized graph facts for one language.</summary>
public interface ICodeGraphPlugin
{
    CodePluginId Id { get; }
    string Version { get; }
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    CodeGraphCapabilities Capabilities { get; }

    bool CanHandle(string path);

    ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
        CodeGraphPluginContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>A repository-aware extraction lifetime owned by one plugin.</summary>
public interface ICodeGraphExtractionSession : IAsyncDisposable
{
    ValueTask ExtractAsync(
        ICodeGraphSink sink,
        CancellationToken cancellationToken = default);
}
