namespace Penghou.Hetu;

/// <summary>Repository-wide inputs available while creating an extraction session.</summary>
public sealed record CodeGraphPluginContext
{
    public CodeGraphPluginContext(
        CodeRepositoryId repositoryId,
        string? repositoryLocation,
        CodeIndexRunId indexRunId,
        IReadOnlyList<CodeGraphSource> sources,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyList<CodeGraphSourceChange>? changes = null)
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

        Changes = changes?.ToArray() ?? [];
        if (Changes.Any(change => change is null))
            throw new ArgumentException("Plugin changes cannot contain null entries.", nameof(changes));
        if (Changes.GroupBy(change => change.Path, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Plugin changes must have unique paths.", nameof(changes));

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
    public IReadOnlyList<CodeGraphSourceChange> Changes { get; }
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

public enum CodeGraphSourceChangeKind
{
    New = 0,
    Changed = 1,
    Unchanged = 2,
    Deleted = 3
}

/// <summary>Describes one exact source transition visible to an extraction plugin.</summary>
public sealed record CodeGraphSourceChange
{
    public CodeGraphSourceChange(
        string path,
        CodeGraphSourceChangeKind kind,
        string? previousHash,
        string? currentHash)
    {
        Path = ContractValue.RelativePath(path, nameof(path));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == CodeGraphSourceChangeKind.New &&
                (previousHash is not null || currentHash is null) ||
            kind == CodeGraphSourceChangeKind.Deleted &&
                (previousHash is null || currentHash is not null) ||
            kind is CodeGraphSourceChangeKind.Changed or CodeGraphSourceChangeKind.Unchanged &&
                (previousHash is null || currentHash is null))
        {
            throw new ArgumentException("Source hashes do not match the change kind.");
        }

        Kind = kind;
        PreviousHash = previousHash;
        CurrentHash = currentHash;
    }

    public string Path { get; }
    public CodeGraphSourceChangeKind Kind { get; }
    public string? PreviousHash { get; }
    public string? CurrentHash { get; }
}

/// <summary>Reports plugin-owned index units that are no longer part of its output.</summary>
public sealed record CodeGraphExtractionResult
{
    public CodeGraphExtractionResult(IReadOnlyCollection<CodeIndexUnitId>? obsoleteIndexUnits = null)
    {
        ObsoleteIndexUnits = obsoleteIndexUnits?
            .Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray() ?? [];
        if (ObsoleteIndexUnits.Any(id => id is null))
            throw new ArgumentException("Obsolete units cannot contain null identities.", nameof(obsoleteIndexUnits));
    }

    public IReadOnlyCollection<CodeIndexUnitId> ObsoleteIndexUnits { get; }
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
    ValueTask<CodeGraphExtractionResult> ExtractAsync(
        ICodeGraphSink sink,
        CancellationToken cancellationToken = default);
}
