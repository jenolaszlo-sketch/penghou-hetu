namespace Penghou.Hetu;

/// <summary>A thread-safe in-memory graph store with atomic index-unit replacement.</summary>
public sealed class InMemoryCodeGraphStore : ICodeGraphStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CodeRepositoryManifest> _repositories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<RunKey, CodeIndexRunManifest> _runs = [];
    private Dictionary<OwnerKey, CodeIndexUnitReplacement> _units = [];

    public ValueTask UpsertRepositoryAsync(
        CodeRepositoryManifest repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _repositories[repository.Id.Value] = repository;
        return ValueTask.CompletedTask;
    }

    public ValueTask<CodeRepositoryManifest?> GetRepositoryAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new(_repositories.GetValueOrDefault(repositoryId.Value));
        }
    }

    public ValueTask StoreIndexRunAsync(
        CodeIndexRunManifest run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_repositories.ContainsKey(run.RepositoryId.Value))
            {
                throw Rejected(
                    CodeGraphValidationErrorKind.OwnershipMismatch,
                    "run.repository.missing",
                    "The index run repository is not registered.");
            }

            var key = new RunKey(run.RepositoryId.Value, run.Id.Value);
            if (_runs.TryGetValue(key, out var existing))
            {
                if (RunsEquivalent(existing, run))
                    return ValueTask.CompletedTask;
                ValidateRunTransition(existing, run);
            }
            _runs[key] = run;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<CodeIndexRunManifest?> GetIndexRunAsync(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(runId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new(_runs.GetValueOrDefault(
                new RunKey(repositoryId.Value, runId.Value)));
        }
    }

    public ValueTask ReplaceIndexUnitAsync(
        CodeIndexUnitReplacement replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureKnownOwnership(replacement.Origin);
            var owner = OwnerKey.From(replacement.Origin);
            var prospective = new Dictionary<OwnerKey, CodeIndexUnitReplacement>(
                _units)
            {
                [owner] = replacement
            };
            var errors = ValidateMaterializedGraph(
                replacement.Origin.RepositoryId,
                prospective);
            if (errors.Count > 0)
                throw new CodeGraphBatchRejectedException(
                    "The index-unit replacement would violate graph invariants.",
                    errors);

            cancellationToken.ThrowIfCancellationRequested();
            _units = prospective;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteIndexUnitAsync(
        CodeRepositoryId repositoryId,
        CodePluginId pluginId,
        CodeIndexUnitId indexUnitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(indexUnitId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var prospective = new Dictionary<OwnerKey, CodeIndexUnitReplacement>(
                _units);
            prospective.Remove(new OwnerKey(
                repositoryId.Value,
                pluginId.Value,
                indexUnitId.Value));
            cancellationToken.ThrowIfCancellationRequested();
            _units = prospective;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<CodeGraphNode?> GetNodeAsync(
        CodeRepositoryId repositoryId,
        CodeNodeId nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(nodeId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var graph = Materialize(repositoryId);
            return new(graph.Nodes.GetValueOrDefault(nodeId.Value));
        }
    }

    public ValueTask<CodeGraphNode?> FindSymbolAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(symbolId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = Materialize(repositoryId).Nodes.Values
                .Where(node => node.SymbolId == symbolId)
                .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            return new(node);
        }
    }

    public ValueTask<IReadOnlyList<CodeGraphNode>> FindNodesByQualifiedNameAsync(
        CodeRepositoryId repositoryId,
        string qualifiedName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new(Materialize(repositoryId).Nodes.Values
                .Where(node => string.Equals(
                    node.QualifiedName,
                    qualifiedName,
                    StringComparison.Ordinal))
                .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(symbolId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new(Materialize(repositoryId).Declarations.Values
                .Where(declaration => declaration.SymbolId == symbolId)
                .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Location.StartLine)
                .ThenBy(declaration => declaration.Location.StartColumn)
                .ThenBy(declaration => declaration.Id.Value, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public ValueTask<CodeGraphTraversalResult> TraverseAsync(
        CodeRepositoryId repositoryId,
        CodeGraphTraversalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var graph = Materialize(repositoryId);
            if (!graph.Nodes.ContainsKey(query.StartNodeId.Value))
                return new(new CodeGraphTraversalResult([], [], false));

            return new(Traverse(graph, query, cancellationToken));
        }
    }

    private void EnsureKnownOwnership(CodeFactOrigin origin)
    {
        if (!_repositories.ContainsKey(origin.RepositoryId.Value))
        {
            throw Rejected(
                CodeGraphValidationErrorKind.OwnershipMismatch,
                "replacement.repository.missing",
                "The replacement repository is not registered.");
        }

        if (!_runs.TryGetValue(
                new RunKey(
                    origin.RepositoryId.Value,
                    origin.IndexRunId.Value),
                out var run) ||
            run.Status != CodeIndexRunStatus.Running)
        {
            throw Rejected(
                CodeGraphValidationErrorKind.OwnershipMismatch,
                "replacement.run.not-running",
                "The replacement index run is not registered and running.");
        }
    }

    private static bool RunsEquivalent(
        CodeIndexRunManifest first,
        CodeIndexRunManifest second) =>
        first.RepositoryId == second.RepositoryId &&
        first.Id == second.Id &&
        first.StartedAt == second.StartedAt &&
        first.Status == second.Status &&
        first.CompletedAt == second.CompletedAt &&
        first.Plugins.Count == second.Plugins.Count &&
        first.Plugins.All(second.Plugins.Contains);

    private static void ValidateRunTransition(
        CodeIndexRunManifest existing,
        CodeIndexRunManifest replacement)
    {
        if (existing.RepositoryId != replacement.RepositoryId ||
            existing.Id != replacement.Id ||
            existing.StartedAt != replacement.StartedAt ||
            existing.Plugins.Count != replacement.Plugins.Count ||
            !existing.Plugins.All(replacement.Plugins.Contains) ||
            existing.Status != CodeIndexRunStatus.Running ||
            replacement.Status == CodeIndexRunStatus.Running)
        {
            throw new InvalidOperationException(
                "Index runs are immutable except for one running-to-terminal transition.");
        }
    }

    private static IReadOnlyList<CodeGraphValidationError> ValidateMaterializedGraph(
        CodeRepositoryId repositoryId,
        IReadOnlyDictionary<OwnerKey, CodeIndexUnitReplacement> units)
    {
        var errors = new List<CodeGraphValidationError>();
        foreach (var unit in units.Values.Where(unit =>
                     unit.Origin.RepositoryId == repositoryId))
        {
            AddDuplicateErrors(
                unit.Nodes.Select(node => node.Id.Value),
                "node",
                errors);
            AddDuplicateErrors(
                unit.Declarations.Select(declaration => declaration.Id.Value),
                "declaration",
                errors);
            AddDuplicateErrors(
                unit.Edges.Select(edge => edge.Id.Value),
                "edge",
                errors);
        }

        _ = Materialize(repositoryId, units, errors);
        return errors.Take(100).ToArray();
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> ids,
        string factKind,
        ICollection<CodeGraphValidationError> errors)
    {
        foreach (var duplicate in ids
                     .GroupBy(id => id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add(Error(
                CodeGraphValidationErrorKind.DuplicateFact,
                $"replacement.{factKind}.duplicate",
                $"An index-unit replacement contains a duplicate {factKind} identity.",
                duplicate));
        }
    }

    private MaterializedGraph Materialize(CodeRepositoryId repositoryId) =>
        Materialize(repositoryId, _units, null);

    private static MaterializedGraph Materialize(
        CodeRepositoryId repositoryId,
        IReadOnlyDictionary<OwnerKey, CodeIndexUnitReplacement> units,
        ICollection<CodeGraphValidationError>? errors)
    {
        var nodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, CodeGraphDeclaration>(
            StringComparer.Ordinal);
        var edges = new Dictionary<string, CodeGraphEdge>(StringComparer.Ordinal);

        foreach (var unit in units
                     .Where(pair => pair.Key.RepositoryId == repositoryId.Value)
                     .OrderBy(pair => pair.Key.PluginId, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.IndexUnitId, StringComparer.Ordinal)
                     .Select(pair => pair.Value))
        {
            Merge(
                unit.Nodes,
                nodes,
                node => node.Id.Value,
                GraphFactEquality.Equivalent,
                "node",
                errors);
            Merge(
                unit.Declarations,
                declarations,
                declaration => declaration.Id.Value,
                GraphFactEquality.Equivalent,
                "declaration",
                errors);
            Merge(
                unit.Edges,
                edges,
                edge => edge.Id.Value,
                GraphFactEquality.Equivalent,
                "edge",
                errors);
        }

        foreach (var duplicate in nodes.Values
                     .Where(node => node.SymbolId is not null)
                     .GroupBy(node => node.SymbolId!.Value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors?.Add(Error(
                CodeGraphValidationErrorKind.DuplicateFact,
                "graph.symbol.duplicate",
                "A semantic symbol identity maps to multiple node identities.",
                duplicate.Key));
        }

        foreach (var declaration in declarations.Values)
        {
            if (!nodes.TryGetValue(
                    declaration.SymbolNodeId.Value,
                    out var symbolNode) ||
                symbolNode.SymbolId != declaration.SymbolId)
            {
                errors?.Add(Error(
                    CodeGraphValidationErrorKind.MissingEndpoint,
                    "graph.declaration.symbol-missing",
                    "A declaration does not reference its matching semantic symbol node.",
                    declaration.Id.Value));
            }
        }

        var visibleEdges = new Dictionary<string, CodeGraphEdge>(StringComparer.Ordinal);
        foreach (var edge in edges.Values)
        {
            if (nodes.ContainsKey(edge.SourceId.Value) &&
                nodes.ContainsKey(edge.TargetId.Value))
            {
                visibleEdges.Add(edge.Id.Value, edge);
            }
            else
            {
                errors?.Add(Error(
                    CodeGraphValidationErrorKind.MissingEndpoint,
                    "graph.edge.endpoint-missing",
                    "An edge endpoint does not exist in the materialized graph.",
                    edge.Id.Value));
            }
        }

        return new MaterializedGraph(nodes, declarations, visibleEdges);
    }

    private static void Merge<T>(
        IEnumerable<T> contributions,
        IDictionary<string, T> materialized,
        Func<T, string> getId,
        Func<T, T, bool> equivalent,
        string factKind,
        ICollection<CodeGraphValidationError>? errors)
    {
        foreach (var contribution in contributions)
        {
            var id = getId(contribution);
            if (!materialized.TryGetValue(id, out var existing))
            {
                materialized.Add(id, contribution);
                continue;
            }

            if (!equivalent(existing, contribution))
            {
                errors?.Add(Error(
                    CodeGraphValidationErrorKind.DuplicateFact,
                    $"graph.{factKind}.conflict",
                    $"Contributions for one {factKind} identity are inconsistent.",
                    id));
            }
        }
    }

    private static CodeGraphTraversalResult Traverse(
        MaterializedGraph graph,
        CodeGraphTraversalQuery query,
        CancellationToken cancellationToken)
    {
        var selectedKinds = query.EdgeKinds
            .Select(kind => kind.Value)
            .ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            query.StartNodeId.Value
        };
        var nodes = new List<CodeGraphNode>
        {
            graph.Nodes[query.StartNodeId.Value]
        };
        var edges = new List<CodeGraphEdge>();
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string NodeId, int Depth)>();
        queue.Enqueue((query.StartNodeId.Value, 0));
        var truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (nodeId, depth) = queue.Dequeue();
            if (depth >= query.MaxDepth)
                continue;

            var candidates = graph.Edges.Values
                .Where(edge => selectedKinds.Count == 0 ||
                    selectedKinds.Contains(edge.Kind.Value))
                .Where(edge => query.Direction switch
                {
                    CodeGraphDirection.Outgoing => edge.SourceId.Value == nodeId,
                    CodeGraphDirection.Incoming => edge.TargetId.Value == nodeId,
                    CodeGraphDirection.Both =>
                        edge.SourceId.Value == nodeId || edge.TargetId.Value == nodeId,
                    _ => false
                })
                .OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal);

            foreach (var edge in candidates)
            {
                var adjacent = edge.SourceId.Value == nodeId
                    ? edge.TargetId.Value
                    : edge.SourceId.Value;
                var isNewNode = !visited.Contains(adjacent);
                if (isNewNode && nodes.Count >= query.MaxNodes)
                {
                    truncated = true;
                    continue;
                }

                if (edgeIds.Add(edge.Id.Value))
                {
                    if (edges.Count >= query.MaxEdges)
                    {
                        truncated = true;
                        break;
                    }

                    edges.Add(edge);
                }

                if (!isNewNode)
                    continue;

                visited.Add(adjacent);
                nodes.Add(graph.Nodes[adjacent]);
                queue.Enqueue((adjacent, depth + 1));
            }

            if (truncated && edges.Count >= query.MaxEdges)
                break;
        }

        return new CodeGraphTraversalResult(nodes, edges, truncated);
    }

    private static CodeGraphBatchRejectedException Rejected(
        CodeGraphValidationErrorKind kind,
        string code,
        string message) =>
        new(message, [Error(kind, code, message)]);

    private static CodeGraphValidationError Error(
        CodeGraphValidationErrorKind kind,
        string code,
        string message,
        string? factId = null) =>
        new(kind, code, message, factId);

    private readonly record struct OwnerKey(
        string RepositoryId,
        string PluginId,
        string IndexUnitId)
    {
        public static OwnerKey From(CodeFactOrigin origin) =>
            new(
                origin.RepositoryId.Value,
                origin.PluginId.Value,
                origin.IndexUnitId.Value);
    }

    private readonly record struct RunKey(string RepositoryId, string RunId);

    private sealed record MaterializedGraph(
        IReadOnlyDictionary<string, CodeGraphNode> Nodes,
        IReadOnlyDictionary<string, CodeGraphDeclaration> Declarations,
        IReadOnlyDictionary<string, CodeGraphEdge> Edges);
}
