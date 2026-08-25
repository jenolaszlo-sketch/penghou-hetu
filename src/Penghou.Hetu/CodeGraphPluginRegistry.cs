namespace Penghou.Hetu;

/// <summary>Immutable, deterministic registry of extraction plugins.</summary>
public sealed class CodeGraphPluginRegistry
{
    private readonly IReadOnlyList<ICodeGraphPlugin> _plugins;

    public CodeGraphPluginRegistry(IEnumerable<ICodeGraphPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _plugins = plugins
            .Select(Validate)
            .OrderBy(plugin => plugin.Language, StringComparer.Ordinal)
            .ThenBy(plugin => plugin.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var duplicate = _plugins
            .GroupBy(plugin => plugin.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Plugin identity '{duplicate.Key}' is registered more than once.",
                nameof(plugins));
        }
    }

    public IReadOnlyList<ICodeGraphPlugin> Plugins => _plugins;

    public IReadOnlyList<ICodeGraphPlugin> FindCandidates(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _plugins.Where(plugin => plugin.CanHandle(path)).ToArray();
    }

    public ICodeGraphPlugin? Resolve(string path)
    {
        var candidates = FindCandidates(path);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new CodeGraphPluginSelectionException(path, candidates)
        };
    }

    private static ICodeGraphPlugin Validate(ICodeGraphPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(plugin.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin.Language);
        ArgumentNullException.ThrowIfNull(plugin.FileExtensions);
        if (plugin.FileExtensions.Count == 0 ||
            plugin.FileExtensions.Any(extension =>
                string.IsNullOrWhiteSpace(extension) ||
                !extension.StartsWith(".", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Plugin '{plugin.Id}' must declare normalized file extensions.",
                nameof(plugin));
        }

        return plugin;
    }
}

/// <summary>Reports that more than one plugin claimed the same path.</summary>
public sealed class CodeGraphPluginSelectionException : Exception
{
    public CodeGraphPluginSelectionException(
        string path,
        IReadOnlyList<ICodeGraphPlugin> candidates)
        : base($"Multiple code graph plugins can handle '{path}'.")
    {
        Path = path;
        CandidateIds = candidates
            .Select(candidate => candidate.Id)
            .ToArray();
    }

    public string Path { get; }
    public IReadOnlyList<CodePluginId> CandidateIds { get; }
}

/// <summary>Fluent standalone construction for the Hetu plugin registry.</summary>
public sealed class HetuBuilder
{
    private readonly List<ICodeGraphPlugin> _plugins = [];
    private readonly List<ICodeRepositoryProvider> _repositoryProviders =
        [new FileSystemCodeRepositoryProvider()];

    public HetuBuilder AddPlugin(ICodeGraphPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugins.Add(plugin);
        return this;
    }

    public CodeGraphPluginRegistry BuildPluginRegistry() => new(_plugins);

    public HetuBuilder AddRepositoryProvider(ICodeRepositoryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _repositoryProviders.Add(provider);
        return this;
    }

    public HetuBuilder ClearRepositoryProviders()
    {
        _repositoryProviders.Clear();
        return this;
    }

    public CodeRepositoryProviderRegistry BuildRepositoryProviderRegistry() =>
        new(_repositoryProviders);
}
