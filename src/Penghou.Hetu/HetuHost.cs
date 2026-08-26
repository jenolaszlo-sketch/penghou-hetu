namespace Penghou.Hetu;

/// <summary>
/// Fluent builder for a complete Hetu host: repository providers, plugins,
/// store selection, indexing, and queries behind one entry point. Hosts and
/// Solo use this instead of manually assembling individual services.
/// </summary>
public sealed class HetuHostBuilder
{
    private readonly List<ICodeGraphPlugin> _plugins = [];
    private readonly List<ICodeRepositoryProvider> _repositoryProviders =
    [
        new FileSystemCodeRepositoryProvider()
    ];
    private Func<ICodeGraphStore>? _storeFactory;
    private CodeIndexingOptions? _indexingOptions;

    /// <summary>Sets the graph store. Call once before Build.</summary>
    public HetuHostBuilder UseStore(Func<ICodeGraphStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(storeFactory);
        _storeFactory = storeFactory;
        return this;
    }

    /// <summary>Adds a language plugin.</summary>
    public HetuHostBuilder AddPlugin(ICodeGraphPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugins.Add(plugin);
        return this;
    }

    /// <summary>Registers a repository provider.</summary>
    public HetuHostBuilder AddRepositoryProvider(ICodeRepositoryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _repositoryProviders.Add(provider);
        return this;
    }

    /// <summary>Removes all repository providers (including the default filesystem provider).</summary>
    public HetuHostBuilder ClearRepositoryProviders()
    {
        _repositoryProviders.Clear();
        return this;
    }

    /// <summary>Sets indexing bounds (concurrency, byte budgets).</summary>
    public HetuHostBuilder WithIndexingOptions(CodeIndexingOptions options)
    {
        _indexingOptions = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>Builds the host.</summary>
    public HetuHost Build()
    {
        var store = _storeFactory?.Invoke() ?? new InMemoryCodeGraphStore();
        var pluginRegistry = new CodeGraphPluginRegistry(_plugins);
        var repositoryRegistry = new CodeRepositoryProviderRegistry(_repositoryProviders);

        return new HetuHost(repositoryRegistry, pluginRegistry, store, _indexingOptions);
    }
}

/// <summary>
/// A ready-to-use Hetu host combining indexing and query services over one
/// configured store. Hosts call IndexAsync to index repositories and use
/// Queries for bounded graph queries.
/// </summary>
public sealed class HetuHost : IAsyncDisposable
{
    private readonly ICodeGraphStore _store;

    internal HetuHost(
        CodeRepositoryProviderRegistry repositories,
        CodeGraphPluginRegistry plugins,
        ICodeGraphStore store,
        CodeIndexingOptions? indexingOptions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Indexing = new CodeIndexingService(repositories, plugins, store);
        Queries = new CodeGraphQueryService(store);
    }

    /// <summary>Bounded semantic queries against the published graph.</summary>
    public CodeGraphQueryService Queries { get; }

    /// <summary>The indexing coordinator for this host.</summary>
    public CodeIndexingService Indexing { get; }

    /// <summary>Read-only store access for advanced scenarios.</summary>
    public ICodeGraphReader Reader => _store;

    /// <summary>Indexes a repository and publishes its graph atomically.</summary>
    public ValueTask<CodeIndexingResult> IndexRepositoryAsync(
        CodeRepositoryDescriptor descriptor,
        CodeIndexRunId runId,
        CodeIndexingOptions? options = null,
        Action<CodeIndexingDiagnostics>? diagnostics = null,
        CancellationToken cancellationToken = default) =>
        Indexing.IndexAsync(descriptor, runId, options, diagnostics, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        switch (_store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
