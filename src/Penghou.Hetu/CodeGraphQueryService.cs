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
    private const int MaximumBatchSymbols = 100;
    private const int MaximumTraversalSeeds = 32;
    private readonly ICodeGraphReader _store;

    public CodeGraphQueryService(ICodeGraphReader store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Gets the latest publication and opens a fail-on-change query view.</summary>
    public async ValueTask<CodeGraphPublicationQuery?> OpenLatestPublicationAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        var publication = await _store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        return publication is null ? null : Bind(publication);
    }

    /// <summary>Creates a query view that rejects results from any other publication.</summary>
    public CodeGraphPublicationQuery Bind(CodeGraphPublication publication) =>
        new(this, publication ?? throw new ArgumentNullException(nameof(publication)));

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

    public async ValueTask<CodeGraphQueryEnvelope<CodeSymbolLookupResult>?>
        FindSymbolWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string qualifiedName,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        if (string.IsNullOrWhiteSpace(qualifiedName))
            throw new ArgumentException("Qualified name is required.", nameof(qualifiedName));
        var envelope = await _store.FindNodesByQualifiedNameWithProvenanceAsync(
            repositoryId,
            qualifiedName,
            cancellationToken).ConfigureAwait(false);
        return envelope is null
            ? null
            : new(
                envelope.Publication,
                envelope.Query,
                new(envelope.Result),
                envelope.Provenance);
    }

    public ValueTask<IReadOnlyList<CodeGraphDeclaration>> FindDeclarationsAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default) =>
        _store.GetDeclarationsAsync(repositoryId, symbolId, cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>
        FindDeclarationsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeSymbolId symbolId,
            CancellationToken cancellationToken = default) =>
        _store.GetDeclarationsWithProvenanceAsync(
            repositoryId,
            symbolId,
            cancellationToken);

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

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>
        GetNeighborhoodWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeNodeId nodeId,
            IReadOnlyCollection<CodeEdgeKind>? edgeKinds = null,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default) =>
        TraverseWithProvenanceAsync(
            repositoryId,
            nodeId,
            CodeGraphDirection.Both,
            edgeKinds ?? [],
            options,
            cancellationToken);

    public ValueTask<CodeGraphTraversalResult> GetImpactSetAsync(
        CodeRepositoryId repositoryId, CodeNodeId nodeId,
        CodeGraphQueryOptions? options = null, CancellationToken cancellationToken = default) =>
        TraverseAsync(repositoryId, nodeId, CodeGraphDirection.Incoming,
            [CodeEdgeKinds.References, CodeEdgeKinds.Calls, CodeEdgeKinds.Implements,
             CodeEdgeKinds.Inherits, CodeEdgeKinds.DependsOn], options, cancellationToken);

    public async ValueTask<CodeGraphMultiTraversalResult> GetImpactSetsAsync(
        CodeRepositoryId repositoryId,
        IReadOnlyCollection<CodeNodeId> seedNodeIds,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await GetImpactSetsWithProvenanceAsync(
            repositoryId,
            seedNodeIds,
            options,
            cancellationToken).ConfigureAwait(false);
        return envelope?.Result ?? new CodeGraphMultiTraversalResult(
            new Dictionary<string, CodeGraphTraversalResult>(
                StringComparer.Ordinal));
    }

    /// <summary>
    /// Resolves many qualified names in one call. Each name maps to its
    /// candidate list (possibly empty); ambiguous names retain all candidates.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<string, CodeSymbolLookupResult>> ResolveSymbolsAsync(
        CodeRepositoryId repositoryId,
        IReadOnlyCollection<string> qualifiedNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        var names = NormalizeNames(qualifiedNames);
        if (names.Length > MaximumBatchSymbols)
        {
            throw new ArgumentOutOfRangeException(
                nameof(qualifiedNames),
                $"A symbol batch cannot exceed {MaximumBatchSymbols} distinct names.");
        }
        var results = new Dictionary<string, CodeSymbolLookupResult>(
            StringComparer.Ordinal);
        foreach (var name in names)
        {
            results[name] = await FindSymbolAsync(
                repositoryId,
                name,
                cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Resolves a bounded batch and returns one envelope only when every lookup
    /// observed the same successful publication.
    /// </summary>
    public async ValueTask<CodeGraphQueryEnvelope<
        IReadOnlyDictionary<string, CodeSymbolLookupResult>>?>
        ResolveSymbolsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            IReadOnlyCollection<string> qualifiedNames,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        var names = NormalizeNames(qualifiedNames);
        if (names.Length > MaximumBatchSymbols)
        {
            throw new ArgumentOutOfRangeException(
                nameof(qualifiedNames),
                $"A symbol batch cannot exceed {MaximumBatchSymbols} distinct names.");
        }

        var publication = await _store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        if (publication is null)
            return null;
        var results = new Dictionary<string, CodeSymbolLookupResult>(
            StringComparer.Ordinal);
        var provenance = new List<CodeGraphFactProvenance>();
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = await FindSymbolWithProvenanceAsync(
                repositoryId,
                name,
                cancellationToken).ConfigureAwait(false);
            EnsurePublication(publication, envelope?.Publication);
            results.Add(name, envelope!.Result);
            provenance.AddRange(envelope.Provenance);
        }

        return new(
            publication,
            new CodeGraphQueryDescriptor(
                "resolve-symbols",
                QualifiedNames: names),
            results,
            DistinctProvenance(provenance));
    }

    /// <summary>
    /// Returns every declaration whose physical location is inside the
    /// specified source file, ordered by position.
    /// </summary>
    public async ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsInFileAsync(
        CodeRepositoryId repositoryId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fileNodes = await _store.FindNodesByQualifiedNameAsync(
            repositoryId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        var fileNode = fileNodes.FirstOrDefault(node =>
            node.Kind == CodeNodeKinds.File);
        if (fileNode is null)
            return [];

        var traversal = await TraverseAsync(
            repositoryId,
            fileNode.Id,
            CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Declares],
            new(maxDepth: 1, maxNodes: 500, maxEdges: 1000),
            cancellationToken).ConfigureAwait(false);

        var symbolIds = traversal.Nodes
            .Where(node => node.SymbolId is not null)
            .Select(node => node.SymbolId!)
            .Distinct()
            .ToArray();

        var declarations = new List<CodeGraphDeclaration>();
        foreach (var symbolId in symbolIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var forSymbol = await _store.GetDeclarationsAsync(
                repositoryId,
                symbolId,
                cancellationToken).ConfigureAwait(false);
            declarations.AddRange(forSymbol.Where(declaration =>
                string.Equals(declaration.Location.Path, sourcePath, StringComparison.Ordinal)));
        }

        return declarations
            .OrderBy(declaration => declaration.Location.StartLine)
            .ThenBy(declaration => declaration.Location.StartColumn)
            .ToArray();
    }

    public async ValueTask<CodeGraphQueryEnvelope<
        IReadOnlyList<CodeGraphDeclaration>>?>
        GetDeclarationsInFileWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string sourcePath,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var publication = await _store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        if (publication is null)
            return null;
        var fileLookup = await _store.FindNodesByQualifiedNameWithProvenanceAsync(
            repositoryId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        EnsurePublication(publication, fileLookup?.Publication);
        var fileNode = fileLookup!.Result.FirstOrDefault(node =>
            node.Kind == CodeNodeKinds.File);
        if (fileNode is null)
        {
            return new(
                publication,
                new CodeGraphQueryDescriptor(
                    "declarations-in-file",
                    QualifiedName: sourcePath),
                [],
                []);
        }

        var traversalQuery = new CodeGraphTraversalQuery(
            fileNode.Id,
            CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Declares],
            maxDepth: 1,
            maxNodes: 500,
            maxEdges: 1000);
        var traversal = await _store.TraverseWithProvenanceAsync(
            repositoryId,
            traversalQuery,
            cancellationToken).ConfigureAwait(false);
        EnsurePublication(publication, traversal?.Publication);
        var declarations = new List<CodeGraphDeclaration>();
        var provenance = new List<CodeGraphFactProvenance>();
        foreach (var symbolId in traversal!.Result.Nodes
                     .Where(node => node.SymbolId is not null)
                     .Select(node => node.SymbolId!)
                     .Distinct())
        {
            var envelope = await _store.GetDeclarationsWithProvenanceAsync(
                repositoryId,
                symbolId,
                cancellationToken).ConfigureAwait(false);
            EnsurePublication(publication, envelope?.Publication);
            declarations.AddRange(envelope!.Result.Where(declaration =>
                string.Equals(
                    declaration.Location.Path,
                    sourcePath,
                    StringComparison.Ordinal)));
            provenance.AddRange(envelope.Provenance);
        }

        var ordered = declarations
            .OrderBy(declaration => declaration.Location.StartLine)
            .ThenBy(declaration => declaration.Location.StartColumn)
            .ToArray();
        var ids = ordered.Select(item => item.Id.Value).ToHashSet(
            StringComparer.Ordinal);
        return new(
            publication,
            new CodeGraphQueryDescriptor(
                "declarations-in-file",
                QualifiedName: sourcePath,
                Traversal: traversalQuery),
            ordered,
            DistinctProvenance(provenance.Where(item =>
                item.Kind == CodeGraphFactKind.Declaration &&
                ids.Contains(item.FactId))));
    }

    /// <summary>
    /// Returns all nodes with public accessibility that are transitively
    /// contained in the specified project, bounded by the query options.
    /// </summary>
    public async ValueTask<IReadOnlyList<CodeGraphNode>> GetPublicSurfaceAsync(
        CodeRepositoryId repositoryId,
        string projectPath,
        CodeGraphQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var projectNodes = await _store.FindNodesByQualifiedNameAsync(
            repositoryId,
            projectPath,
            cancellationToken).ConfigureAwait(false);
        var projectNode = projectNodes.FirstOrDefault(node =>
            node.Kind == CodeNodeKinds.Project);
        if (projectNode is null)
            return [];

        options ??= new(maxDepth: 10, maxNodes: 2000, maxEdges: 5000);
        var traversal = await TraverseAsync(
            repositoryId,
            projectNode.Id,
            CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Contains, CodeEdgeKinds.Declares],
            options,
            cancellationToken).ConfigureAwait(false);

        return SelectPublicSurface(traversal.Nodes);
    }

    public async ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>
        GetPublicSurfaceWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string projectPath,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var publication = await _store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        if (publication is null)
            return null;
        var projectLookup = await _store.FindNodesByQualifiedNameWithProvenanceAsync(
            repositoryId,
            projectPath,
            cancellationToken).ConfigureAwait(false);
        EnsurePublication(publication, projectLookup?.Publication);
        var projectNode = projectLookup!.Result.FirstOrDefault(node =>
            node.Kind == CodeNodeKinds.Project);
        if (projectNode is null)
        {
            return new(
                publication,
                new CodeGraphQueryDescriptor(
                    "public-surface",
                    QualifiedName: projectPath),
                [],
                []);
        }

        options ??= new(maxDepth: 10, maxNodes: 2000, maxEdges: 5000);
        var traversalQuery = new CodeGraphTraversalQuery(
            projectNode.Id,
            CodeGraphDirection.Outgoing,
            [CodeEdgeKinds.Contains, CodeEdgeKinds.Declares],
            options.EvidenceKinds,
            options.MaxDepth,
            options.MaxNodes,
            options.MaxEdges);
        var traversal = await _store.TraverseWithProvenanceAsync(
            repositoryId,
            traversalQuery,
            cancellationToken).ConfigureAwait(false);
        EnsurePublication(publication, traversal?.Publication);
        var result = SelectPublicSurface(traversal!.Result.Nodes);
        var ids = result.Select(node => node.Id.Value).ToHashSet(
            StringComparer.Ordinal);
        return new(
            publication,
            new CodeGraphQueryDescriptor(
                "public-surface",
                QualifiedName: projectPath,
                Traversal: traversalQuery),
            result,
            DistinctProvenance(traversal.Provenance.Where(item =>
                item.Kind == CodeGraphFactKind.Node &&
                ids.Contains(item.FactId))));
    }

    public async ValueTask<CodeGraphQueryEnvelope<CodeGraphMultiTraversalResult>?>
        GetImpactSetsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            IReadOnlyCollection<CodeNodeId> seedNodeIds,
            CodeGraphQueryOptions? options = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(seedNodeIds);
        var seeds = seedNodeIds
            .Where(seed => seed is not null)
            .Distinct()
            .OrderBy(seed => seed.Value, StringComparer.Ordinal)
            .ToArray();
        if (seeds.Length > MaximumTraversalSeeds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seedNodeIds),
                $"An impact batch cannot exceed {MaximumTraversalSeeds} distinct seeds.");
        }

        var publication = await _store.GetLatestPublicationAsync(
            repositoryId,
            cancellationToken).ConfigureAwait(false);
        if (publication is null)
            return null;
        options ??= new();
        var queries = seeds.Select(seed => new CodeGraphTraversalQuery(
            seed,
            CodeGraphDirection.Incoming,
            [
                CodeEdgeKinds.References,
                CodeEdgeKinds.Calls,
                CodeEdgeKinds.Implements,
                CodeEdgeKinds.Inherits,
                CodeEdgeKinds.DependsOn
            ],
            options.EvidenceKinds,
            options.MaxDepth,
            options.MaxNodes,
            options.MaxEdges)).ToArray();
        var results = new Dictionary<string, CodeGraphTraversalResult>(
            StringComparer.Ordinal);
        var provenance = new List<CodeGraphFactProvenance>();
        foreach (var query in queries)
        {
            var envelope = await _store.TraverseWithProvenanceAsync(
                repositoryId,
                query,
                cancellationToken).ConfigureAwait(false);
            EnsurePublication(publication, envelope?.Publication);
            results.Add(query.StartNodeId.Value, envelope!.Result);
            provenance.AddRange(envelope.Provenance);
        }

        return new(
            publication,
            new CodeGraphQueryDescriptor(
                "impact-sets",
                Traversals: queries),
            new CodeGraphMultiTraversalResult(results),
            DistinctProvenance(provenance));
    }

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

    internal ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>
        TraverseWithProvenanceAsync(
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
        return _store.TraverseWithProvenanceAsync(
            repositoryId,
            new(
                nodeId,
                direction,
                edgeKinds,
                options.EvidenceKinds,
                options.MaxDepth,
                options.MaxNodes,
                options.MaxEdges),
            cancellationToken);
    }

    private static IReadOnlyList<CodeGraphNode> SelectPublicSurface(
        IEnumerable<CodeGraphNode> nodes) =>
        nodes
            .Where(node =>
                node.Kind != CodeNodeKinds.Project &&
                node.Kind != CodeNodeKinds.File)
            .Where(node =>
                node.Properties.TryGetValue("access", out var access) &&
                access is CodeTextProperty { Value: "public" })
            .OrderBy(
                node => node.QualifiedName ?? node.Name,
                StringComparer.Ordinal)
            .ToArray();

    private static string[] NormalizeNames(
        IReadOnlyCollection<string> qualifiedNames)
    {
        ArgumentNullException.ThrowIfNull(qualifiedNames);
        return qualifiedNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static void EnsurePublication(
        CodeGraphPublication expected,
        CodeGraphPublication? actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (actual != expected)
            throw new CodeGraphPublicationChangedException(expected, actual);
    }

    private static IReadOnlyList<CodeGraphFactProvenance> DistinctProvenance(
        IEnumerable<CodeGraphFactProvenance> provenance) =>
        provenance
            .GroupBy(item => (item.Kind, item.FactId))
            .Select(group => new CodeGraphFactProvenance(
                group.Key.Kind,
                group.Key.FactId,
                group.SelectMany(item => item.Contributors)
                    .Distinct()
                    .OrderBy(origin => origin.PluginId.Value, StringComparer.Ordinal)
                    .ThenBy(origin => origin.PluginVersion, StringComparer.Ordinal)
                    .ThenBy(origin => origin.IndexUnitId.Value, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.FactId, StringComparer.Ordinal)
            .ToArray();
}
