namespace Penghou.Hetu;

/// <summary>Immutable deterministic registry of repository source providers.</summary>
public sealed class CodeRepositoryProviderRegistry
{
    private readonly IReadOnlyList<ICodeRepositoryProvider> _providers;

    public CodeRepositoryProviderRegistry(
        IEnumerable<ICodeRepositoryProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers
            .Select(Validate)
            .OrderBy(provider => provider.Name, StringComparer.Ordinal)
            .ToArray();
        var duplicate = _providers
            .GroupBy(provider => provider.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Repository provider '{duplicate.Key}' is registered more than once.",
                nameof(providers));
        }
    }

    public IReadOnlyList<ICodeRepositoryProvider> Providers => _providers;

    public IReadOnlyList<ICodeRepositoryProvider> FindCandidates(
        CodeRepositoryDescriptor repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return _providers
            .Where(provider => provider.CanOpen(repository))
            .ToArray();
    }

    public ICodeRepositoryProvider? Resolve(CodeRepositoryDescriptor repository)
    {
        var candidates = FindCandidates(repository);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new CodeRepositoryProviderSelectionException(
                repository,
                candidates)
        };
    }

    public async ValueTask<ICodeRepositorySource> OpenAsync(
        CodeRepositoryDescriptor repository,
        CancellationToken cancellationToken = default)
    {
        var provider = Resolve(repository) ??
            throw new CodeRepositoryProviderNotFoundException(repository);
        return await provider.OpenAsync(repository, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ICodeRepositoryProvider Validate(
        ICodeRepositoryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
        return provider;
    }
}

public sealed class CodeRepositoryProviderSelectionException : Exception
{
    public CodeRepositoryProviderSelectionException(
        CodeRepositoryDescriptor repository,
        IReadOnlyList<ICodeRepositoryProvider> candidates)
        : base($"Multiple repository providers can open '{repository.Location}'.")
    {
        Repository = repository;
        CandidateNames = candidates.Select(candidate => candidate.Name).ToArray();
    }

    public CodeRepositoryDescriptor Repository { get; }
    public IReadOnlyList<string> CandidateNames { get; }
}

public sealed class CodeRepositoryProviderNotFoundException : Exception
{
    public CodeRepositoryProviderNotFoundException(
        CodeRepositoryDescriptor repository)
        : base($"No repository provider can open '{repository.Location}'.") =>
        Repository = repository;

    public CodeRepositoryDescriptor Repository { get; }
}
