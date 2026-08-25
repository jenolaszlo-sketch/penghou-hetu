using System.Text.Json;
using System.Text.Json.Serialization;
using LadybugDB;

namespace Penghou.Hetu;

/// <summary>Durable embedded Hetu store backed by LadybugDB.</summary>
public sealed class LadybugCodeGraphStore : ICodeGraphStore, IDisposable
{
    public const int CurrentSchemaVersion = 4;

    private readonly Database _database;
    private readonly Connection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PersistedCommand> _commands;
    private InMemoryCodeGraphStore _inner;
    private bool _disposed;
    private readonly Action<string>? _faultInjector;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public LadybugCodeGraphStore(string databasePath) : this(databasePath, null) { }

    internal LadybugCodeGraphStore(string databasePath, Action<string>? faultInjector)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A Ladybug database path is required.", nameof(databasePath));
        _database = new Database(databasePath);
        _connection = new Connection(_database);
        _faultInjector = faultInjector;
        InitializeSchema();
        _commands = LoadCommands();
        _inner = ReplayAsync(_commands, CancellationToken.None).GetAwaiter().GetResult();
    }

    public ValueTask UpsertRepositoryAsync(CodeRepositoryManifest repository, CancellationToken cancellationToken = default) =>
        MutateAsync(new("repository", Repository: repository), cancellationToken);

    public ValueTask<CodeRepositoryManifest?> GetRepositoryAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        _inner.GetRepositoryAsync(repositoryId, cancellationToken);

    public ValueTask StoreIndexRunAsync(CodeIndexRunManifest run, CancellationToken cancellationToken = default) =>
        MutateAsync(new("run", Run: run), cancellationToken);

    public ValueTask<CodeIndexRunManifest?> GetIndexRunAsync(CodeRepositoryId repositoryId, CodeIndexRunId runId, CancellationToken cancellationToken = default) =>
        _inner.GetIndexRunAsync(repositoryId, runId, cancellationToken);

    public ValueTask CompleteIndexRunAsync(CodeIndexRunManifest completedRun, CodeRepositoryIndexState state, CancellationToken cancellationToken = default) =>
        MutateAsync(new("complete", Run: completedRun, State: state), cancellationToken);

    public ValueTask<CodeRepositoryIndexState?> GetLatestIndexStateAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        _inner.GetLatestIndexStateAsync(repositoryId, cancellationToken);

    public ValueTask StageIndexUnitAsync(CodeIndexUnitReplacement replacement, CancellationToken cancellationToken = default) =>
        MutateAsync(new("stage-replace", Replacement: replacement), cancellationToken);

    public ValueTask StageIndexUnitDeletionAsync(CodeRepositoryId repositoryId, CodeIndexRunId indexRunId, CodePluginId pluginId, CodeIndexUnitId indexUnitId, CancellationToken cancellationToken = default) =>
        MutateAsync(new("stage-delete", RepositoryId: repositoryId, RunId: indexRunId, PluginId: pluginId, UnitId: indexUnitId), cancellationToken);

    public ValueTask<CodeGraphNode?> GetNodeAsync(CodeRepositoryId repositoryId, CodeNodeId nodeId, CancellationToken cancellationToken = default) =>
        _inner.GetNodeAsync(repositoryId, nodeId, cancellationToken);

    public async ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>
        FindNodesByQualifiedNameWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string qualifiedName,
            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.FindNodesByQualifiedNameWithProvenanceAsync(
                repositoryId,
                qualifiedName,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>
        TraverseWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeGraphTraversalQuery query,
            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.TraverseWithProvenanceAsync(
                repositoryId,
                query,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>
        GetDeclarationsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeSymbolId symbolId,
            CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.GetDeclarationsWithProvenanceAsync(
                repositoryId,
                symbolId,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CodeGraphNode?> FindSymbolAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadSingle<CodeGraphNode>("HetuNode", $"s.repositoryId = '{Key(repositoryId.Value)}' AND s.symbolId = '{Key(symbolId.Value)}'"); }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<CodeGraphNode>> FindNodesByQualifiedNameAsync(CodeRepositoryId repositoryId, string qualifiedName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadMany<CodeGraphNode>("HetuNode", $"s.repositoryId = '{Key(repositoryId.Value)}' AND s.qualifiedName = '{Key(qualifiedName)}'")
                .GroupBy(node => node.Id).Select(group => group.First()).OrderBy(node => node.Id.Value, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadMany<CodeGraphDeclaration>("HetuDeclaration", $"s.repositoryId = '{Key(repositoryId.Value)}' AND s.symbolId = '{Key(symbolId.Value)}'")
                .GroupBy(value => value.Id).Select(group => group.First()).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CodeGraphTraversalResult> TraverseAsync(CodeRepositoryId repositoryId, CodeGraphTraversalQuery query, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = await _inner.GetNodeAsync(repositoryId, query.StartNodeId, cancellationToken).ConfigureAwait(false);
            if (start is null)
                return new([], [], false);
            var nodes = new List<CodeGraphNode> { start };
            var edges = new List<CodeGraphEdge>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { start.Id.Value };
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<(CodeNodeId Id, int Depth)>();
            queue.Enqueue((start.Id, 0));
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
                    var omitted = ReadTraversalEdges(repositoryId, nodeId, query).Count;
                    if (omitted > 0)
                    {
                        truncated = true;
                        truncationReason |= CodeGraphTruncationReason.MaxDepth;
                        omittedFrontierCount += omitted;
                    }
                    continue;
                }
                foreach (var edge in ReadTraversalEdges(repositoryId, nodeId, query))
                {
                    edgesExamined++;
                    var adjacentId = edge.SourceId == nodeId ? edge.TargetId : edge.SourceId;
                    var isNew = !visited.Contains(adjacentId.Value);
                    if (isNew && nodes.Count >= query.MaxNodes)
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
                    if (!isNew)
                        continue;
                    var adjacent = await _inner.GetNodeAsync(repositoryId, adjacentId, cancellationToken).ConfigureAwait(false);
                    if (adjacent is null)
                        throw new InvalidDataException("Ladybug edge references a missing materialized node.");
                    visited.Add(adjacentId.Value);
                    nodes.Add(adjacent);
                    queue.Enqueue((adjacentId, depth + 1));
                }
                if (truncated && edges.Count >= query.MaxEdges)
                    break;
            }
            return new(
                nodes,
                edges,
                truncated,
                truncationReason,
                depthReached,
                nodesExamined,
                edgesExamined,
                omittedFrontierCount);
        }
        finally { _gate.Release(); }
    }

    public LadybugCodeGraphStoreHealth CheckHealth()
    {
        ThrowIfDisposed();
        using var result = _connection.Query("MATCH (s:HetuMetadata) RETURN s.schemaVersion LIMIT 1");
        var row = result.Rows().FirstOrDefault();
        var version = row is null
            ? CurrentSchemaVersion
            : ParseVersion(row[0]?.ToString());
        return new(version == CurrentSchemaVersion, version, Count("HetuRepository"), Count("HetuRun"), Count("HetuUnit"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
        _connection.Dispose();
        _database.Dispose();
    }

    private async ValueTask MutateAsync(PersistedCommand command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = Apply(_commands, command);
            await ApplyCommandAsync(_inner, command, cancellationToken).ConfigureAwait(false);
            try
            {
                Persist(command, next);
                _commands = next;
            }
            catch
            {
                _inner = await ReplayAsync(_commands, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Persist(PersistedCommand command, IReadOnlyList<PersistedCommand> next)
    {
        var affectedAdjacency = AffectedAdjacencyNodes(_commands, command);
        Execute("BEGIN TRANSACTION");
        try
        {
            switch (command.Kind)
            {
                case "repository": Upsert("HetuRepository", Key(command.Repository!.Id.Value), Serialize(command.Repository)); break;
                case "run": Upsert("HetuRun", RunKey(command.Run!), Serialize(command.Run)); break;
                case "complete":
                    PublishStagedRun(command.Run!, _commands);
                    Upsert("HetuRun", RunKey(command.Run!), Serialize(command.Run));
                    Upsert("HetuIndexState", Key(command.State!.RepositoryId.Value), Serialize(command.State));
                    break;
                case "stage-replace":
                    Upsert("HetuStage", StageKey(command), Serialize(command));
                    break;
                case "stage-delete":
                    Upsert("HetuStage", StageKey(command), Serialize(command));
                    break;
                case "replace":
                    Upsert("HetuUnit", UnitKey(command.Replacement!.Origin), Serialize(command.Replacement));
                    ReplaceFacts(command.Replacement);
                    break;
                case "delete":
                    var unitKey = UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!);
                    Delete("HetuUnit", unitKey);
                    DeleteFacts(unitKey);
                    break;
                default: throw new InvalidDataException($"Unknown persisted Hetu command '{command.Kind}'.");
            }
            if (command.Kind == "run" && command.Run!.Status != CodeIndexRunStatus.Running)
                DeleteStagedRun(command.Run);
            if (affectedAdjacency.Count > 0)
                UpdateAdjacency(next, affectedAdjacency);
            _faultInjector?.Invoke("before-commit");
            Execute("COMMIT");
        }
        catch
        {
            try { Execute("ROLLBACK"); } catch { }
            throw;
        }
    }

    private List<PersistedCommand> LoadCommands()
    {
        var repositories = ReadPayloads<CodeRepositoryManifest>("HetuRepository")
            .Select(value => new PersistedCommand("repository", Repository: value));
        var runs = ReadPayloads<CodeIndexRunManifest>("HetuRun").ToArray();
        var units = ReadPayloads<CodeIndexUnitReplacement>("HetuUnit")
            .Select(value => new PersistedCommand("replace", Replacement: value));
        var stages = ReadPayloads<PersistedCommand>("HetuStage");
        var states = ReadPayloads<CodeRepositoryIndexState>("HetuIndexState")
            .ToDictionary(state => state.RepositoryId.Value, StringComparer.Ordinal);
        var terminals = runs.Where(run => run.Status != CodeIndexRunStatus.Running)
            .Select(run => run.Status == CodeIndexRunStatus.Completed &&
                    states.TryGetValue(run.RepositoryId.Value, out var state) && state.IndexRunId == run.Id
                ? new PersistedCommand("complete", Run: run, State: state)
                : new PersistedCommand("run", Run: run));
        var running = runs.Where(run => run.Status == CodeIndexRunStatus.Running)
            .Select(run => new PersistedCommand("run", Run: run));
        return repositories.Concat(running).Concat(terminals).Concat(units).Concat(stages).ToList();
    }

    private void InitializeSchema()
    {
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuMetadata(id INT64, schemaVersion INT64, PRIMARY KEY(id))");
        using (var result = _connection.Query("MATCH (s:HetuMetadata) RETURN s.schemaVersion LIMIT 1"))
        {
            var row = result.Rows().FirstOrDefault();
            if (row is null)
                Execute($"CREATE (:HetuMetadata {{id: 1, schemaVersion: {CurrentSchemaVersion}}})");
            else
            {
                var version = ParseVersion(row[0]?.ToString());
                if (version != CurrentSchemaVersion)
                    throw new LadybugCodeGraphSchemaException(version, CurrentSchemaVersion);
            }
        }
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuRepository(key STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuRun(key STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuIndexState(key STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuUnit(key STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuStage(key STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuNode(key STRING, repositoryId STRING, unitKey STRING, nodeId STRING, symbolId STRING, qualifiedName STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuDeclaration(key STRING, repositoryId STRING, unitKey STRING, declarationId STRING, symbolId STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuEdge(key STRING, repositoryId STRING, unitKey STRING, edgeId STRING, sourceId STRING, targetId STRING, kind STRING, evidenceKind INT64, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuAdjacency(key STRING, payload STRING, PRIMARY KEY(key))");
    }

    private IEnumerable<T> ReadPayloads<T>(string table)
    {
        using var result = _connection.Query($"MATCH (s:{table}) RETURN s.key, s.payload ORDER BY s.key");
        foreach (var row in result.Rows())
        {
            var payload = row[1]?.ToString() ?? throw new InvalidDataException($"Ladybug {table} payload is missing.");
            yield return JsonSerializer.Deserialize<T>(Convert.FromBase64String(payload), SerializerOptions) ??
                throw new InvalidDataException($"Ladybug {table} payload is invalid.");
        }
    }

    private void Upsert(string table, string key, string payload)
    {
        Delete(table, key);
        Execute($"CREATE (:{table} {{key: '{key}', payload: '{payload}'}})");
    }

    private void Delete(string table, string key) => Execute($"MATCH (s:{table}) WHERE s.key = '{key}' DELETE s");

    private void ReplaceFacts(CodeIndexUnitReplacement replacement)
    {
        const int statementBatchSize = 100;
        var unitKey = UnitKey(replacement.Origin);
        DeleteFacts(unitKey);
        var repositoryId = Key(replacement.Origin.RepositoryId.Value);
        ExecuteCreates(replacement.Nodes.Select(node =>
            $"(:HetuNode {{key: '{FactKey(unitKey, node.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', nodeId: '{Key(node.Id.Value)}', symbolId: '{Key(node.SymbolId?.Value ?? string.Empty)}', qualifiedName: '{Key(node.QualifiedName ?? string.Empty)}', payload: '{Serialize(node)}'}})"), statementBatchSize);
        ExecuteCreates(replacement.Declarations.Select(declaration =>
            $"(:HetuDeclaration {{key: '{FactKey(unitKey, declaration.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', declarationId: '{Key(declaration.Id.Value)}', symbolId: '{Key(declaration.SymbolId.Value)}', payload: '{Serialize(declaration)}'}})"), statementBatchSize);
        ExecuteCreates(replacement.Edges.Select(edge =>
            $"(:HetuEdge {{key: '{FactKey(unitKey, edge.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', edgeId: '{Key(edge.Id.Value)}', sourceId: '{Key(edge.SourceId.Value)}', targetId: '{Key(edge.TargetId.Value)}', kind: '{Key(edge.Kind.Value)}', evidenceKind: {(int)edge.Evidence.Kind}, payload: '{Serialize(edge)}'}})"), statementBatchSize);
    }

    private void ExecuteCreates(IEnumerable<string> patterns, int batchSize)
    {
        foreach (var batch in patterns.Chunk(batchSize))
            Execute("CREATE " + string.Join(", ", batch));
    }

    private void DeleteFacts(string unitKey)
    {
        Execute($"MATCH (s:HetuNode) WHERE s.unitKey = '{unitKey}' DELETE s");
        Execute($"MATCH (s:HetuDeclaration) WHERE s.unitKey = '{unitKey}' DELETE s");
        Execute($"MATCH (s:HetuEdge) WHERE s.unitKey = '{unitKey}' DELETE s");
    }

    private void PublishStagedRun(
        CodeIndexRunManifest run,
        IReadOnlyList<PersistedCommand> commands)
    {
        foreach (var staged in commands.Where(command => MatchesRun(command, run)))
        {
            if (staged.Kind == "stage-replace")
            {
                Upsert("HetuUnit", UnitKey(staged.Replacement!.Origin), Serialize(staged.Replacement));
                ReplaceFacts(staged.Replacement);
            }
            else
            {
                var unitKey = UnitKey(staged.RepositoryId!, staged.PluginId!, staged.UnitId!);
                Delete("HetuUnit", unitKey);
                DeleteFacts(unitKey);
            }
            Delete("HetuStage", StageKey(staged));
        }
    }

    private void DeleteStagedRun(CodeIndexRunManifest run)
    {
        foreach (var staged in _commands.Where(command => MatchesRun(command, run)))
            Delete("HetuStage", StageKey(staged));
    }

    private T? ReadSingle<T>(string table, string predicate) =>
        ReadMany<T>(table, predicate).FirstOrDefault();

    private IReadOnlyList<T> ReadMany<T>(string table, string predicate)
    {
        using var result = _connection.Query($"MATCH (s:{table}) WHERE {predicate} RETURN s.payload ORDER BY s.key");
        return result.Rows().Select(row =>
        {
            var payload = row[0]?.ToString() ?? throw new InvalidDataException($"Ladybug {table} payload is missing.");
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(payload), SerializerOptions) ??
                throw new InvalidDataException($"Ladybug {table} payload is invalid.");
        }).ToArray();
    }

    private IReadOnlyList<CodeGraphEdge> ReadTraversalEdges(
        CodeRepositoryId repositoryId,
        CodeNodeId nodeId,
        CodeGraphTraversalQuery query)
    {
        var directions = query.Direction switch
        {
            CodeGraphDirection.Outgoing => new[] { "out" },
            CodeGraphDirection.Incoming => new[] { "in" },
            CodeGraphDirection.Both => new[] { "out", "in" },
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        var edges = directions.SelectMany(direction =>
            ReadAdjacency(AdjacencyKey(repositoryId, nodeId, direction)));
        return edges
            .Where(edge => query.EdgeKinds.Count == 0 || query.EdgeKinds.Contains(edge.Kind))
            .Where(edge => query.EvidenceKinds.Count == 0 || query.EvidenceKinds.Contains(edge.Evidence.Kind))
            .GroupBy(edge => edge.Id)
            .Select(group => group.First())
            .OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<CodeGraphEdge> ReadAdjacency(string key)
    {
        using var result = _connection.Query($"MATCH (s:HetuAdjacency) WHERE s.key = '{key}' RETURN s.payload");
        var row = result.Rows().FirstOrDefault();
        if (row is null)
            return [];
        var payload = row[0]?.ToString() ?? throw new InvalidDataException("Ladybug adjacency payload is missing.");
        return JsonSerializer.Deserialize<IReadOnlyList<CodeGraphEdge>>(
            Convert.FromBase64String(payload), SerializerOptions) ??
            throw new InvalidDataException("Ladybug adjacency payload is invalid.");
    }

    private void UpdateAdjacency(
        IReadOnlyList<PersistedCommand> commands,
        IReadOnlySet<(string RepositoryId, string NodeId)> affected)
    {
        const int statementBatchSize = 100;
        var edges = commands
            .Where(command => command.Kind == "replace")
            .SelectMany(command => command.Replacement!.Edges.Select(edge =>
                (RepositoryId: command.Replacement.Origin.RepositoryId.Value, Edge: edge)))
            .GroupBy(value => $"{value.RepositoryId}\n{value.Edge.Id.Value}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var records = new List<(string Key, string? Payload)>();
        foreach (var (repositoryId, nodeId) in affected.OrderBy(value => value.RepositoryId, StringComparer.Ordinal)
                     .ThenBy(value => value.NodeId, StringComparer.Ordinal))
        {
            var repository = new CodeRepositoryId(repositoryId);
            var node = new CodeNodeId(nodeId);
            records.Add(AdjacencyRecord(repository, node, "out", edges
                .Where(value => value.RepositoryId == repositoryId && value.Edge.SourceId == node)
                .Select(value => value.Edge)));
            records.Add(AdjacencyRecord(repository, node, "in", edges
                .Where(value => value.RepositoryId == repositoryId && value.Edge.TargetId == node)
                .Select(value => value.Edge)));
        }
        foreach (var batch in records.Chunk(statementBatchSize))
        {
            Execute("MATCH (s:HetuAdjacency) WHERE " +
                string.Join(" OR ", batch.Select(record => $"s.key = '{record.Key}'")) +
                " DELETE s");
            var populated = batch.Where(record => record.Payload is not null).ToArray();
            if (populated.Length > 0)
            {
                Execute("CREATE " + string.Join(", ", populated.Select(record =>
                    $"(:HetuAdjacency {{key: '{record.Key}', payload: '{record.Payload}'}})")));
            }
        }
    }

    private static (string Key, string? Payload) AdjacencyRecord(
        CodeRepositoryId repositoryId,
        CodeNodeId nodeId,
        string direction,
        IEnumerable<CodeGraphEdge> edges)
    {
        var key = AdjacencyKey(repositoryId, nodeId, direction);
        var values = edges.OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal).ToArray();
        return (key, values.Length == 0 ? null : Serialize(values));
    }

    private static HashSet<(string RepositoryId, string NodeId)> AffectedAdjacencyNodes(
        IReadOnlyList<PersistedCommand> current,
        PersistedCommand command)
    {
        var affected = new HashSet<(string, string)>();
        if (command.Kind == "complete")
        {
            foreach (var replacement in current
                         .Where(existing => existing.Kind == "replace" || MatchesRun(existing, command.Run!))
                         .Select(existing => existing.Replacement)
                         .Where(replacement => replacement is not null && replacement.Origin.RepositoryId == command.Run!.RepositoryId))
            {
                foreach (var edge in replacement!.Edges)
                {
                    affected.Add((replacement.Origin.RepositoryId.Value, edge.SourceId.Value));
                    affected.Add((replacement.Origin.RepositoryId.Value, edge.TargetId.Value));
                }
            }
            return affected;
        }
        IEnumerable<CodeIndexUnitReplacement> replacements = current
            .Where(existing => existing.Kind == "replace" && SameSlot(existing, command))
            .Select(existing => existing.Replacement!);
        if (command.Kind == "replace")
            replacements = replacements.Append(command.Replacement!);
        foreach (var replacement in replacements)
            foreach (var edge in replacement.Edges)
            {
                affected.Add((replacement.Origin.RepositoryId.Value, edge.SourceId.Value));
                affected.Add((replacement.Origin.RepositoryId.Value, edge.TargetId.Value));
            }
        return affected;
    }

    private int Count(string table)
    {
        using var result = _connection.Query($"MATCH (s:{table}) RETURN count(s)");
        var row = result.Rows().FirstOrDefault();
        return row is null ? 0 : ParseVersion(row[0]?.ToString());
    }

    private static string Serialize<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));
    private static string Key(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    private static string RunKey(CodeIndexRunManifest run) => Key($"{run.RepositoryId.Value}\n{run.Id.Value}");
    private static string UnitKey(CodeFactOrigin origin) => UnitKey(origin.RepositoryId, origin.PluginId, origin.IndexUnitId);
    private static string FactKey(string unitKey, string factId) => Key($"{unitKey}\n{factId}");
    private static string AdjacencyKey(CodeRepositoryId repositoryId, CodeNodeId nodeId, string direction) =>
        Key($"{repositoryId.Value}\n{direction}\n{nodeId.Value}");
    private static string UnitKey(CodeRepositoryId repositoryId, CodePluginId pluginId, CodeIndexUnitId unitId) =>
        Key($"{repositoryId.Value}\n{pluginId.Value}\n{unitId.Value}");

    private static List<PersistedCommand> Apply(IReadOnlyList<PersistedCommand> current, PersistedCommand command)
    {
        if (command.Kind == "complete")
        {
            var publishedNext = current.Where(existing => !MatchesRun(existing, command.Run!)).ToList();
            foreach (var staged in current.Where(existing => MatchesRun(existing, command.Run!)))
            {
                var published = staged.Kind == "stage-replace"
                    ? new PersistedCommand("replace", Replacement: staged.Replacement)
                    : new PersistedCommand("delete", RepositoryId: staged.RepositoryId, PluginId: staged.PluginId, UnitId: staged.UnitId);
                publishedNext = Apply(publishedNext, published);
            }
            publishedNext = publishedNext.Where(existing => !SameSlot(existing, command)).ToList();
            publishedNext.Add(command);
            return publishedNext;
        }

        var next = current.Where(existing => !SameSlot(existing, command)).ToList();
        if (command.Kind != "delete")
            next.Add(command);
        if (command.Kind == "run" && command.Run!.Status != CodeIndexRunStatus.Running)
            next.RemoveAll(existing => MatchesRun(existing, command.Run));
        return next;
    }

    private static bool SameSlot(PersistedCommand existing, PersistedCommand command) => command.Kind switch
    {
        "repository" => existing.Kind == "repository" && existing.Repository!.Id == command.Repository!.Id,
        "run" or "complete" => existing.Kind is "run" or "complete" && existing.Run!.RepositoryId == command.Run!.RepositoryId && existing.Run.Id == command.Run.Id,
        "replace" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.Replacement!.Origin),
        "delete" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!),
        "stage-replace" => existing.Kind is "stage-replace" or "stage-delete" && StageKey(existing) == StageKey(command),
        "stage-delete" => existing.Kind is "stage-replace" or "stage-delete" && StageKey(existing) == StageKey(command),
        _ => false
    };

    private static bool MatchesRun(PersistedCommand command, CodeIndexRunManifest run) =>
        command.Kind is "stage-replace" or "stage-delete" &&
        (command.Replacement?.Origin.RepositoryId ?? command.RepositoryId) == run.RepositoryId &&
        (command.Replacement?.Origin.IndexRunId ?? command.RunId) == run.Id;

    private static string StageKey(PersistedCommand command)
    {
        var origin = command.Replacement?.Origin;
        return Key($"{origin?.RepositoryId.Value ?? command.RepositoryId!.Value}\n{origin?.IndexRunId.Value ?? command.RunId!.Value}\n{origin?.PluginId.Value ?? command.PluginId!.Value}\n{origin?.IndexUnitId.Value ?? command.UnitId!.Value}");
    }

    private void Execute(string query)
    {
        using var result = _connection.Query(query);
    }

    private static int ParseVersion(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var version)
            ? version
            : throw new InvalidDataException("Ladybug Hetu schema version is missing or invalid.");

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new RepositoryManifestConverter());
        return options;
    }

    private static async Task<InMemoryCodeGraphStore> ReplayAsync(
        IReadOnlyList<PersistedCommand> commands,
        CancellationToken cancellationToken)
    {
        var store = new InMemoryCodeGraphStore();
        foreach (var command in commands.Where(command => command.Kind == "repository"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpsertRepositoryAsync(command.Repository!, cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind is "run" or "complete"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = command.Run!;
            await store.StoreIndexRunAsync(
                run.Status == CodeIndexRunStatus.Running
                    ? run
                    : new CodeIndexRunManifest(run.RepositoryId, run.Id, run.StartedAt, plugins: run.Plugins),
                cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind == "replace"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.RestorePublishedIndexUnitAsync(command.Replacement!, cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind is "stage-replace" or "stage-delete"))
            await ApplyCommandAsync(store, command, cancellationToken);
        foreach (var command in commands.Where(command => command.Kind is "run" or "complete" &&
                     command.Run!.Status != CodeIndexRunStatus.Running))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Kind == "complete")
                await store.CompleteIndexRunAsync(command.Run!, command.State!, cancellationToken);
            else
                await store.StoreIndexRunAsync(command.Run!, cancellationToken);
        }
        return store;
    }

    private static async Task ApplyCommandAsync(
        InMemoryCodeGraphStore store,
        PersistedCommand command,
        CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case "repository": await store.UpsertRepositoryAsync(command.Repository!, cancellationToken); break;
            case "run": await store.StoreIndexRunAsync(command.Run!, cancellationToken); break;
            case "complete": await store.CompleteIndexRunAsync(command.Run!, command.State!, cancellationToken); break;
            case "stage-replace": await store.StageIndexUnitAsync(command.Replacement!, cancellationToken); break;
            case "stage-delete": await store.StageIndexUnitDeletionAsync(command.RepositoryId!, command.RunId!, command.PluginId!, command.UnitId!, cancellationToken); break;
            default: throw new InvalidDataException($"Unknown persisted Hetu command '{command.Kind}'.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PersistedCommand(
        string Kind,
        CodeRepositoryManifest? Repository = null,
        CodeIndexRunManifest? Run = null,
        CodeRepositoryIndexState? State = null,
        CodeIndexUnitReplacement? Replacement = null,
        CodeRepositoryId? RepositoryId = null,
        CodeIndexRunId? RunId = null,
        CodePluginId? PluginId = null,
        CodeIndexUnitId? UnitId = null);

    private sealed class RepositoryManifestConverter : JsonConverter<CodeRepositoryManifest>
    {
        public override CodeRepositoryManifest Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return new(
                new CodeRepositoryId(root.GetProperty("Id").GetProperty("Value").GetString()!),
                root.TryGetProperty("DisplayName", out var name) ? name.GetString() : null,
                root.TryGetProperty("SourceUri", out var uri) ? uri.GetString() : null,
                root.GetProperty("RegisteredAt").GetDateTimeOffset());
        }

        public override void Write(
            Utf8JsonWriter writer,
            CodeRepositoryManifest value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("Id");
            writer.WriteString("Value", value.Id.Value);
            writer.WriteEndObject();
            writer.WriteString("DisplayName", value.DisplayName);
            writer.WriteString("SourceUri", value.SourceUri);
            writer.WriteString("RegisteredAt", value.RegisteredAt);
            writer.WriteEndObject();
        }
    }
}

public sealed record LadybugCodeGraphStoreHealth(bool IsHealthy, int SchemaVersion, int RepositoryCount, int RunCount, int IndexUnitCount);

public sealed class LadybugCodeGraphSchemaException : Exception
{
    public LadybugCodeGraphSchemaException(int actualVersion, int expectedVersion)
        : base($"Ladybug Hetu schema version {actualVersion} is incompatible with expected version {expectedVersion}.")
    {
        ActualVersion = actualVersion;
        ExpectedVersion = expectedVersion;
    }

    public int ActualVersion { get; }
    public int ExpectedVersion { get; }
}
