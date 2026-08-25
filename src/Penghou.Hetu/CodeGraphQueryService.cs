namespace Penghou.Hetu;

/// <summary>Common bounds and evidence filters for provider-neutral graph queries.</summary>
public sealed record CodeGraphQueryOptions
{
    public CodeGraphQueryOptions(
        int maxDepth = 1,
        int maxNodes = 100,
        int maxEdges = 250,
        IReadOnlyCollection<CodeEvidenceKind>? evidenceKinds = null)
    {
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (maxEdges < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEdges));
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        MaxEdges = maxEdges;
        EvidenceKinds = evidenceKinds?.Distinct().ToArray() ?? [];
    }

    public int MaxDepth { get; }
    public int MaxNodes { get; }
    public int MaxEdges { get; }
    public IReadOnlyCollection<CodeEvidenceKind> EvidenceKinds { get; }
}

/// <summary>An exact qualified-name lookup that preserves ambiguity.</summary>
public sealed record CodeSymbolLookupResult(IReadOnlyList<CodeGraphNode> Candidates)
{
    public bool IsAmbiguous => Candidates.Count > 1;
    public CodeGraphNode? SingleOrDefault => Candidates.Count == 1 ? Candidates[0] : null;
}

/// <summary>Provides bounded semantic queries without exposing store-specific languages.</summary>
public sealed class CodeGraphQueryService
{
    private readonly ICodeGraphStore _store;

    public CodeGraphQueryService(ICodeGraphStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<CodeSymbolLookupResult> FindSymbolAsync(
        CodeRepositoryId repositoryId,
        string qualifiedName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        if (string.IsNullOrWhiteSpace(qualifiedName))
            throw new ArgumentException("Qualified name is required.", nameof(qualifiedName));
        var candidates = await _store.FindNodesByQualifiedNameAsync(
            repositoryId,
            qualifiedName,
            cancellationToken).ConfigureAwait(false);
        return new(candidates.OrderBy(node => node.Id.Value, StringComparer.Ordinal).ToArray());
    }

    public ValueTask<IReadOnlyList<CodeGraphDeclaration>> FindDeclarationsAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default) =>
        _store.GetDeclarationsAsync(repositoryId, symbolId, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindReferencesAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.References], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindCallersAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.Calls], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindCalleesAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Calls], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindImplementationsAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.Implements, CodeEdgeKinds.Inherits], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindDependenciesAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.DependsOn], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> FindDependentsAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.DependsOn], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> GetNeighborhoodAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        IReadOnlyCollection<CodeEdgeKind>? edgeKinds = null,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Both,
            edgeKinds ?? [], options, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> GetImpactSetAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.References, CodeEdgeKinds.Calls, CodeEdgeKinds.Implements,
             CodeEdgeKinds.Inherits, CodeEdgeKinds.DependsOn], options, cancellationToken);

    private ValueTask<CodeGraphTraversalResult> TraverseAsync(
        CodeRepositoryId repositoryId,
        CodeNodeId nodeId,
        CodeGraphDirection direction,
        IReadOnlyCollection<CodeEdgeKind> edgeKinds,
        CodeGraphQueryOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(nodeId);
        options ??= new();
        return _store.TraverseAsync(
            repositoryId,
            new CodeGraphTraversalQuery(
                nodeId,
                direction,
                edgeKinds,
                options.EvidenceKinds,
                options.MaxDepth,
                options.MaxNodes,
                options.MaxEdges),
            cancellationToken);
    }
}
