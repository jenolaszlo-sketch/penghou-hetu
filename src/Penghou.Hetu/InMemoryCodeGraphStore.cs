namespace Penghou.Hetu;

/// <summary>A thread-safe in-memory graph store with atomic index-unit replacement.</summary>
public sealed class InMemoryCodeGraphStore : ICodeGraphStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CodeRepositoryManifest> _repositories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<RunKey, CodeIndexRunManifest> _runs = [];
    private readonly Dictionary<string, CodeRepositoryIndexState> _indexStates =
        new(StringComparer.Ordinal);
    private Dictionary<OwnerKey, CodeIndexUnitReplacement> _units = [];
    private readonly Dictionary<RunKey, StagedRun> _staged = [];
    private readonly Dictionary<string, MaterializedGraph> _materialized =
        new(StringComparer.Ordinal);

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
        if (run.Status == CodeIndexRunStatus.Completed)
        {
            throw new ArgumentException(
                "Successful runs must be stored with CompleteIndexRunAsync.",
                nameof(run));
        }

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
            if (run.Status is CodeIndexRunStatus.Failed or CodeIndexRunStatus.Cancelled)
                _staged.Remove(key);
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

    public ValueTask CompleteIndexRunAsync(
        CodeIndexRunManifest completedRun,
        CodeRepositoryIndexState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedRun);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        if (completedRun.Status != CodeIndexRunStatus.Completed ||
            completedRun.CompletedAt is null)
        {
            throw new ArgumentException(
                "Atomic index completion requires a completed run manifest.",
                nameof(completedRun));
        }

        if (state.RepositoryId != completedRun.RepositoryId ||
            state.IndexRunId != completedRun.Id)
        {
            throw new ArgumentException(
                "Index state ownership must match the completed run.",
                nameof(state));
        }

        lock (_gate)
        {
            var key = new RunKey(completedRun.RepositoryId.Value, completedRun.Id.Value);
            if (!_runs.TryGetValue(key, out var running))
            {
                throw new InvalidOperationException(
                    "The index run must be registered before it can be completed.");
            }

            if (RunsEquivalent(running, completedRun))
            {
                if (_indexStates.TryGetValue(state.RepositoryId.Value, out var existing) &&
                    StatesEquivalent(existing, state))
                {
                    return ValueTask.CompletedTask;
                }

                throw new InvalidOperationException(
                    "A completed index run cannot publish different incremental state.");
            }

            ValidateRunTransition(running, completedRun);
            var prospective = ApplyStagedChanges(key);
            var errors = ValidateMaterializedGraph(completedRun.RepositoryId, prospective);
            if (errors.Count > 0)
            {
                throw new CodeGraphBatchRejectedException(
                    "The staged index run would violate graph invariants.",
                    errors);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _units = prospective;
            _materialized.Remove(completedRun.RepositoryId.Value);
            _runs[key] = completedRun;
            _indexStates[state.RepositoryId.Value] = state;
            _staged.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<CodeRepositoryIndexState?> GetLatestIndexStateAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_indexStates.GetValueOrDefault(repositoryId.Value));
    }

    public ValueTask StageIndexUnitAsync(
        CodeIndexUnitReplacement replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureKnownOwnership(replacement.Origin);
            var owner = OwnerKey.From(replacement.Origin);
            var runKey = new RunKey(
                replacement.Origin.RepositoryId.Value,
                replacement.Origin.IndexRunId.Value);
            var staged = GetOrCreateStagedRun(runKey);
            var prospective = ApplyStagedChanges(runKey);
            prospective[owner] = replacement;
            var errors = ValidateMaterializedGraph(
                replacement.Origin.RepositoryId,
                prospective);
            if (errors.Count > 0)
                throw new CodeGraphBatchRejectedException(
                    "The index-unit replacement would violate graph invariants.",
                    errors);

            cancellationToken.ThrowIfCancellationRequested();
            staged.Deletions.Remove(owner);
            staged.Replacements[owner] = replacement;
        }

        return ValueTask.CompletedTask;
    }

    internal ValueTask RestorePublishedIndexUnitAsync(
        CodeIndexUnitReplacement replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var prospective = new Dictionary<OwnerKey, CodeIndexUnitReplacement>(_units)
            {
                [OwnerKey.From(replacement.Origin)] = replacement
            };
            var errors = ValidateMaterializedGraph(replacement.Origin.RepositoryId, prospective);
            if (errors.Count > 0)
                throw new CodeGraphBatchRejectedException("The restored graph is invalid.", errors);
            _units = prospective;
            _materialized.Remove(replacement.Origin.RepositoryId.Value);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask StageIndexUnitDeletionAsync(
        CodeRepositoryId repositoryId,
        CodeIndexRunId indexRunId,
        CodePluginId pluginId,
        CodeIndexUnitId indexUnitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(indexRunId);
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(indexUnitId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var runKey = new RunKey(repositoryId.Value, indexRunId.Value);
            EnsureRunningRun(runKey, pluginId);
            var owner = new OwnerKey(
                repositoryId.Value,
                pluginId.Value,
                indexUnitId.Value);
            cancellationToken.ThrowIfCancellationRequested();
            var staged = GetOrCreateStagedRun(runKey);
            staged.Replacements.Remove(owner);
            staged.Deletions.Add(owner);
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

    public ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>
        FindNodesByQualifiedNameWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string qualifiedName,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetPublication(repositoryId, out var publication))
                return ValueTask.FromResult<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>(null);
            var graph = Materialize(repositoryId);
            var nodes = graph.Nodes.Values
                .Where(node => string.Equals(node.QualifiedName, qualifiedName, StringComparison.Ordinal))
                .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>(
                new CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>(
                    publication,
                    new("qualified-name", qualifiedName),
                    nodes,
                    ProvenanceForNodes(graph, nodes).ToArray()));
        }
    }

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>
        TraverseWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeGraphTraversalQuery query,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetPublication(repositoryId, out var publication))
                return ValueTask.FromResult<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>(null);
            var graph = Materialize(repositoryId);
            var result = graph.Nodes.ContainsKey(query.StartNodeId.Value)
                ? Traverse(graph, query, cancellationToken)
                : new CodeGraphTraversalResult([], [], false);
            var provenance = ProvenanceForNodes(graph, result.Nodes)
                .Concat(ProvenanceForEdges(graph, result.Edges))
                .ToArray();
            return ValueTask.FromResult<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>(
                new CodeGraphQueryEnvelope<CodeGraphTraversalResult>(
                    publication,
                    new("traversal", Traversal: query),
                    result,
                    provenance));
        }
    }

    public ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>
        GetDeclarationsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeSymbolId symbolId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(symbolId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetPublication(repositoryId, out var publication))
                return ValueTask.FromResult<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>(null);
            var graph = Materialize(repositoryId);
            var declarations = graph.Declarations.Values
                .Where(declaration => declaration.SymbolId == symbolId)
                .OrderBy(declaration => declaration.Location.Path, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Location.StartLine)
                .ThenBy(declaration => declaration.Location.StartColumn)
                .ThenBy(declaration => declaration.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var provenance = declarations.Select(declaration => new CodeGraphFactProvenance(
                CodeGraphFactKind.Declaration,
                declaration.Id.Value,
                graph.DeclarationOrigins.GetValueOrDefault(declaration.Id.Value) ?? [])).ToArray();
            return ValueTask.FromResult<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>(
                new CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>(
                    publication,
                    new("declarations"),
                    declarations,
                    provenance));
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

        if (!run.Plugins.Contains(origin.PluginId))
        {
            throw Rejected(
                CodeGraphValidationErrorKind.OwnershipMismatch,
                "replacement.plugin.not-in-run",
                "The replacement plugin is not registered for the index run.");
        }
    }

    private void EnsureRunningRun(RunKey key, CodePluginId pluginId)
    {
        if (!_repositories.ContainsKey(key.RepositoryId) ||
            !_runs.TryGetValue(key, out var run) ||
            run.Status != CodeIndexRunStatus.Running ||
            !run.Plugins.Contains(pluginId))
        {
            throw Rejected(
                CodeGraphValidationErrorKind.OwnershipMismatch,
                "deletion.run.not-running",
                "The deletion index run and plugin must be registered and running.");
        }
    }

    private StagedRun GetOrCreateStagedRun(RunKey key)
    {
        if (!_staged.TryGetValue(key, out var staged))
        {
            staged = new StagedRun();
            _staged.Add(key, staged);
        }
        return staged;
    }

    private Dictionary<OwnerKey, CodeIndexUnitReplacement> ApplyStagedChanges(RunKey key)
    {
        var prospective = new Dictionary<OwnerKey, CodeIndexUnitReplacement>(_units);
        if (!_staged.TryGetValue(key, out var staged))
            return prospective;
        foreach (var owner in staged.Deletions)
            prospective.Remove(owner);
        foreach (var replacement in staged.Replacements)
            prospective[replacement.Key] = replacement.Value;
        return prospective;
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

    private static bool StatesEquivalent(
        CodeRepositoryIndexState first,
        CodeRepositoryIndexState second) =>
        first.RepositoryId == second.RepositoryId &&
        first.IndexRunId == second.IndexRunId &&
        first.SnapshotIdentity == second.SnapshotIdentity &&
        first.IsConsistentSnapshot == second.IsConsistentSnapshot &&
        first.Sources.Count == second.Sources.Count &&
        first.Sources.Zip(second.Sources).All(pair =>
            pair.First.PluginId == pair.Second.PluginId &&
            pair.First.PluginVersion == pair.Second.PluginVersion &&
            pair.First.SourcePath == pair.Second.SourcePath &&
            pair.First.SourceHash == pair.Second.SourceHash);

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

    private MaterializedGraph Materialize(CodeRepositoryId repositoryId)
    {
        if (!_materialized.TryGetValue(repositoryId.Value, out var graph))
        {
            graph = Materialize(repositoryId, _units, null);
            _materialized.Add(repositoryId.Value, graph);
        }
        return graph;
    }

    private static MaterializedGraph Materialize(
        CodeRepositoryId repositoryId,
        IReadOnlyDictionary<OwnerKey, CodeIndexUnitReplacement> units,
        ICollection<CodeGraphValidationError>? errors)
    {
        var nodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, CodeGraphDeclaration>(
            StringComparer.Ordinal);
        var edges = new Dictionary<string, CodeGraphEdge>(StringComparer.Ordinal);
        var nodeOrigins = new Dictionary<string, List<CodeFactOrigin>>(StringComparer.Ordinal);
        var declarationOrigins = new Dictionary<string, List<CodeFactOrigin>>(StringComparer.Ordinal);
        var edgeOrigins = new Dictionary<string, List<CodeFactOrigin>>(StringComparer.Ordinal);

        foreach (var unit in units
                     .Where(pair => pair.Key.RepositoryId == repositoryId.Value)
                     .OrderBy(pair => pair.Key.PluginId, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.IndexUnitId, StringComparer.Ordinal)
                     .Select(pair => pair.Value))
        {
            foreach (var node in unit.Nodes)
                AddOrigin(nodeOrigins, node.Id.Value, unit.Origin);
            foreach (var declaration in unit.Declarations)
                AddOrigin(declarationOrigins, declaration.Id.Value, unit.Origin);
            foreach (var edge in unit.Edges)
                AddOrigin(edgeOrigins, edge.Id.Value, unit.Origin);
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

        var outgoing = visibleEdges.Values
            .GroupBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CodeGraphEdge>)OrderEdges(group).ToArray(),
                StringComparer.Ordinal);
        var incoming = visibleEdges.Values
            .GroupBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CodeGraphEdge>)OrderEdges(group).ToArray(),
                StringComparer.Ordinal);
        return new MaterializedGraph(
            nodes,
            declarations,
            visibleEdges,
            outgoing,
            incoming,
            FreezeOrigins(nodeOrigins),
            FreezeOrigins(declarationOrigins),
            FreezeOrigins(edgeOrigins));
    }

    private static void AddOrigin(
        IDictionary<string, List<CodeFactOrigin>> origins,
        string factId,
        CodeFactOrigin origin)
    {
        if (!origins.TryGetValue(factId, out var values))
        {
            values = [];
            origins.Add(factId, values);
        }
        if (!values.Contains(origin))
            values.Add(origin);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CodeFactOrigin>> FreezeOrigins(
        IReadOnlyDictionary<string, List<CodeFactOrigin>> origins) =>
        origins.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CodeFactOrigin>)pair.Value
                .OrderBy(origin => origin.PluginId.Value, StringComparer.Ordinal)
                .ThenBy(origin => origin.IndexUnitId.Value, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);

    private static IOrderedEnumerable<CodeGraphEdge> OrderEdges(
        IEnumerable<CodeGraphEdge> edges) =>
        edges.OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal);

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
        var selectedEvidence = query.EvidenceKinds.ToHashSet();
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
        var truncationReason = CodeGraphTruncationReason.None;
        var depthReached = 0;
        var nodesExamined = 0;
        var edgesExamined = 0;
        var omittedFrontierCount = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (nodeId, depth) = queue.Dequeue();
            nodesExamined++;
            depthReached = Math.Max(depthReached, depth);
            if (depth >= query.MaxDepth)
            {
                var omitted = AdjacentEdges(graph, nodeId, query.Direction)
                    .Count(edge =>
                        (selectedKinds.Count == 0 || selectedKinds.Contains(edge.Kind.Value)) &&
                        (selectedEvidence.Count == 0 || selectedEvidence.Contains(edge.Evidence.Kind)));
                if (omitted > 0)
                {
                    truncated = true;
                    truncationReason |= CodeGraphTruncationReason.MaxDepth;
                    omittedFrontierCount += omitted;
                }
                continue;
            }

            var candidates = AdjacentEdges(graph, nodeId, query.Direction)
                .Where(edge => selectedKinds.Count == 0 ||
                    selectedKinds.Contains(edge.Kind.Value))
                .Where(edge => selectedEvidence.Count == 0 ||
                    selectedEvidence.Contains(edge.Evidence.Kind))
                .DistinctBy(edge => edge.Id.Value)
                .OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
                .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal);

            foreach (var edge in candidates)
            {
                edgesExamined++;
                var adjacent = edge.SourceId.Value == nodeId
                    ? edge.TargetId.Value
                    : edge.SourceId.Value;
                var isNewNode = !visited.Contains(adjacent);
                if (isNewNode && nodes.Count >= query.MaxNodes)
                {
                    truncated = true;
                    truncationReason |= CodeGraphTruncationReason.MaxNodes;
                    omittedFrontierCount++;
                    continue;
                }

                if (edgeIds.Add(edge.Id.Value))
                {
                    if (edges.Count >= query.MaxEdges)
                    {
                        truncated = true;
                        truncationReason |= CodeGraphTruncationReason.MaxEdges;
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

        return new CodeGraphTraversalResult(
            nodes,
            edges,
            truncated,
            truncationReason,
            depthReached,
            nodesExamined,
            edgesExamined,
            omittedFrontierCount);
    }

    private static IEnumerable<CodeGraphEdge> AdjacentEdges(
        MaterializedGraph graph,
        string nodeId,
        CodeGraphDirection direction) => direction switch
        {
            CodeGraphDirection.Outgoing => graph.Outgoing.GetValueOrDefault(nodeId) ?? [],
            CodeGraphDirection.Incoming => graph.Incoming.GetValueOrDefault(nodeId) ?? [],
            CodeGraphDirection.Both =>
                (graph.Outgoing.GetValueOrDefault(nodeId) ?? [])
                .Concat(graph.Incoming.GetValueOrDefault(nodeId) ?? []),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

    private bool TryGetPublication(
        CodeRepositoryId repositoryId,
        out CodeGraphPublication publication)
    {
        if (_indexStates.TryGetValue(repositoryId.Value, out var state))
        {
            publication = new(
                repositoryId,
                state.IndexRunId,
                state.SnapshotIdentity,
                state.IsConsistentSnapshot);
            return true;
        }
        publication = null!;
        return false;
    }

    private static IEnumerable<CodeGraphFactProvenance> ProvenanceForNodes(
        MaterializedGraph graph,
        IEnumerable<CodeGraphNode> nodes) =>
        nodes.Select(node => new CodeGraphFactProvenance(
            CodeGraphFactKind.Node,
            node.Id.Value,
            graph.NodeOrigins.GetValueOrDefault(node.Id.Value) ?? []));

    private static IEnumerable<CodeGraphFactProvenance> ProvenanceForEdges(
        MaterializedGraph graph,
        IEnumerable<CodeGraphEdge> edges) =>
        edges.Select(edge => new CodeGraphFactProvenance(
            CodeGraphFactKind.Edge,
            edge.Id.Value,
            graph.EdgeOrigins.GetValueOrDefault(edge.Id.Value) ?? []));

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

    private sealed class StagedRun
    {
        public Dictionary<OwnerKey, CodeIndexUnitReplacement> Replacements { get; } = [];
        public HashSet<OwnerKey> Deletions { get; } = [];
    }

    private sealed record MaterializedGraph(
        IReadOnlyDictionary<string, CodeGraphNode> Nodes,
        IReadOnlyDictionary<string, CodeGraphDeclaration> Declarations,
        IReadOnlyDictionary<string, CodeGraphEdge> Edges,
        IReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>> Outgoing,
        IReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>> Incoming,
        IReadOnlyDictionary<string, IReadOnlyList<CodeFactOrigin>> NodeOrigins,
        IReadOnlyDictionary<string, IReadOnlyList<CodeFactOrigin>> DeclarationOrigins,
        IReadOnlyDictionary<string, IReadOnlyList<CodeFactOrigin>> EdgeOrigins);
}
