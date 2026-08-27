namespace Penghou.Hetu;

/// <summary>
/// A fail-on-change query view bound to one successful publication. Hetu keeps
/// only the latest graph today, so this view detects publication movement
/// rather than pretending to retain historical data.
/// </summary>
public sealed class CodeGraphPublicationQuery
{
    private readonly CodeGraphQueryService _queries;

    internal CodeGraphPublicationQuery(
        CodeGraphQueryService queries,
        CodeGraphPublication publication)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        Publication = publication ??
            throw new ArgumentNullException(nameof(publication));
    }

    public CodeGraphPublication Publication { get; }

    public async ValueTask<CodeGraphQueryEnvelope<CodeSymbolLookupResult>>
        FindSymbolAsync(
            string qualifiedName,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.FindSymbolWithProvenanceAsync(
            Publication.RepositoryId,
            qualifiedName,
            cancellationToken).ConfigureAwait(false));

    public async ValueTask<CodeGraphQueryEnvelope<
        IReadOnlyDictionary<string, CodeSymbolLookupResult>>>
        ResolveSymbolsAsync(
            IReadOnlyCollection<string> qualifiedNames,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.ResolveSymbolsWithProvenanceAsync(
            Publication.RepositoryId,
            qualifiedNames,
            cancellationToken).ConfigureAwait(false));

    public async ValueTask<CodeGraphQueryEnvelope<
        IReadOnlyList<CodeGraphDeclaration>>>
        FindDeclarationsAsync(
            CodeSymbolId symbolId,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.FindDeclarationsWithProvenanceAsync(
            Publication.RepositoryId,
            symbolId,
            cancellationToken).ConfigureAwait(false));

    public async ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>>
        GetNeighborhoodAsync(
            CodeNodeId nodeId,
            IReadOnlyCollection<CodeEdgeKind>? edgeKinds = null,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.GetNeighborhoodWithProvenanceAsync(
            Publication.RepositoryId,
            nodeId,
            edgeKinds,
            options,
            cancellationToken).ConfigureAwait(false));

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindReferencesAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.References], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindCallersAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.Calls], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindCalleesAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Calls], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindImplementationsAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.Implements, CodeEdgeKinds.Inherits], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindDependenciesAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.DependsOn], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> FindDependentsAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.DependsOn], options, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> GetImpactSetAsync(
        CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TraverseAsync(
            nodeId,
            CodeGraphDirection.Incoming,
            [
                CodeEdgeKinds.References,
                CodeEdgeKinds.Calls,
                CodeEdgeKinds.Implements,
                CodeEdgeKinds.Inherits,
                CodeEdgeKinds.DependsOn
            ],
            options,
            cancellationToken);

    public async ValueTask<CodeGraphQueryEnvelope<CodeGraphMultiTraversalResult>>
        GetImpactSetsAsync(
            IReadOnlyCollection<CodeNodeId> seedNodeIds,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.GetImpactSetsWithProvenanceAsync(
            Publication.RepositoryId,
            seedNodeIds,
            options,
            cancellationToken).ConfigureAwait(false));

    public async ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>>
        GetPublicSurfaceAsync(
            string projectPath,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.GetPublicSurfaceWithProvenanceAsync(
            Publication.RepositoryId,
            projectPath,
            options,
            cancellationToken).ConfigureAwait(false));

    public async ValueTask<CodeGraphQueryEnvelope<
        IReadOnlyList<CodeGraphDeclaration>>>
        GetDeclarationsInFileAsync(
            string sourcePath,
            CancellationToken cancellationToken = default) =>
        Require(await _queries.GetDeclarationsInFileWithProvenanceAsync(
            Publication.RepositoryId,
            sourcePath,
            cancellationToken).ConfigureAwait(false));

    private CodeGraphQueryEnvelope<TResult> Require<TResult>(
        CodeGraphQueryEnvelope<TResult>? envelope)
    {
        CodeGraphQueryService.EnsurePublication(
            Publication,
            envelope?.Publication);
        return envelope!;
    }

    private async ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>> TraverseAsync(
        CodeNodeId nodeId,
        CodeGraphDirection direction,
        IReadOnlyCollection<CodeEdgeKind> edgeKinds,
        CodeGraphQueryOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return Require(await _queries.TraverseWithProvenanceAsync(
            Publication.RepositoryId,
            nodeId,
            direction,
            edgeKinds,
            options,
            cancellationToken).ConfigureAwait(false));
    }
}
