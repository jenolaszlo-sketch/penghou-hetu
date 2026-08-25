using System.Text.Json;
using System.Text.Json.Serialization;
using LadybugDB;

namespace Penghou.Hetu;

/// <summary>Durable embedded Hetu store backed by LadybugDB.</summary>
public sealed class LadybugCodeGraphStore : ICodeGraphStore, IDisposable
{
    public const int CurrentSchemaVersion = 2;

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

    public ValueTask ReplaceIndexUnitAsync(CodeIndexUnitReplacement replacement, CancellationToken cancellationToken = default) =>
        MutateAsync(new("replace", Replacement: replacement), cancellationToken);

    public ValueTask DeleteIndexUnitAsync(CodeRepositoryId repositoryId, CodePluginId pluginId, CodeIndexUnitId indexUnitId, CancellationToken cancellationToken = default) =>
        MutateAsync(new("delete", RepositoryId: repositoryId, PluginId: pluginId, UnitId: indexUnitId), cancellationToken);

    public ValueTask<CodeGraphNode?> GetNodeAsync(CodeRepositoryId repositoryId, CodeNodeId nodeId, CancellationToken cancellationToken = default) =>
        _inner.GetNodeAsync(repositoryId, nodeId, cancellationToken);

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
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (nodeId, depth) = queue.Dequeue();
                if (depth >= query.MaxDepth)
                    continue;
                foreach (var edge in ReadTraversalEdges(repositoryId, nodeId, query))
                {
                    var adjacentId = edge.SourceId == nodeId ? edge.TargetId : edge.SourceId;
                    var isNew = !visited.Contains(adjacentId.Value);
                    if (isNew && nodes.Count >= query.MaxNodes) { truncated = true; continue; }
                    if (edgeIds.Add(edge.Id.Value))
                    {
                        if (edges.Count >= query.MaxEdges) { truncated = true; break; }
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
            return new(nodes, edges, truncated);
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
                Persist(command);
                _commands = next;
                _inner = await ReplayAsync(next, CancellationToken.None).ConfigureAwait(false);
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

    private void Persist(PersistedCommand command)
    {
        Execute("BEGIN TRANSACTION");
        try
        {
            switch (command.Kind)
            {
                case "repository": Upsert("HetuRepository", Key(command.Repository!.Id.Value), Serialize(command.Repository)); break;
                case "run": Upsert("HetuRun", RunKey(command.Run!), Serialize(command.Run)); break;
                case "complete":
                    Upsert("HetuRun", RunKey(command.Run!), Serialize(command.Run));
                    Upsert("HetuIndexState", Key(command.State!.RepositoryId.Value), Serialize(command.State));
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
        var running = runs.Select(run => new PersistedCommand("run", Run:
            run.Status == CodeIndexRunStatus.Running ? run : new CodeIndexRunManifest(
                run.RepositoryId, run.Id, run.StartedAt, plugins: run.Plugins)));
        var units = ReadPayloads<CodeIndexUnitReplacement>("HetuUnit")
            .Select(value => new PersistedCommand("replace", Replacement: value));
        var states = ReadPayloads<CodeRepositoryIndexState>("HetuIndexState")
            .ToDictionary(state => state.RepositoryId.Value, StringComparer.Ordinal);
        var terminals = runs.Where(run => run.Status != CodeIndexRunStatus.Running)
            .Select(run => run.Status == CodeIndexRunStatus.Completed &&
                    states.TryGetValue(run.RepositoryId.Value, out var state) && state.IndexRunId == run.Id
                ? new PersistedCommand("complete", Run: run, State: state)
                : new PersistedCommand("run", Run: run));
        return repositories.Concat(running).Concat(units).Concat(terminals).ToList();
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
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuNode(key STRING, repositoryId STRING, unitKey STRING, nodeId STRING, symbolId STRING, qualifiedName STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuDeclaration(key STRING, repositoryId STRING, unitKey STRING, declarationId STRING, symbolId STRING, payload STRING, PRIMARY KEY(key))");
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuEdge(key STRING, repositoryId STRING, unitKey STRING, edgeId STRING, sourceId STRING, targetId STRING, kind STRING, evidenceKind INT64, payload STRING, PRIMARY KEY(key))");
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
        var unitKey = UnitKey(replacement.Origin);
        DeleteFacts(unitKey);
        var repositoryId = Key(replacement.Origin.RepositoryId.Value);
        foreach (var node in replacement.Nodes)
        {
            Execute($"CREATE (:HetuNode {{key: '{FactKey(unitKey, node.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', nodeId: '{Key(node.Id.Value)}', symbolId: '{Key(node.SymbolId?.Value ?? string.Empty)}', qualifiedName: '{Key(node.QualifiedName ?? string.Empty)}', payload: '{Serialize(node)}'}})");
        }
        foreach (var declaration in replacement.Declarations)
        {
            Execute($"CREATE (:HetuDeclaration {{key: '{FactKey(unitKey, declaration.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', declarationId: '{Key(declaration.Id.Value)}', symbolId: '{Key(declaration.SymbolId.Value)}', payload: '{Serialize(declaration)}'}})");
        }
        foreach (var edge in replacement.Edges)
        {
            Execute($"CREATE (:HetuEdge {{key: '{FactKey(unitKey, edge.Id.Value)}', repositoryId: '{repositoryId}', unitKey: '{unitKey}', edgeId: '{Key(edge.Id.Value)}', sourceId: '{Key(edge.SourceId.Value)}', targetId: '{Key(edge.TargetId.Value)}', kind: '{Key(edge.Kind.Value)}', evidenceKind: {(int)edge.Evidence.Kind}, payload: '{Serialize(edge)}'}})");
        }
    }

    private void DeleteFacts(string unitKey)
    {
        Execute($"MATCH (s:HetuNode) WHERE s.unitKey = '{unitKey}' DELETE s");
        Execute($"MATCH (s:HetuDeclaration) WHERE s.unitKey = '{unitKey}' DELETE s");
        Execute($"MATCH (s:HetuEdge) WHERE s.unitKey = '{unitKey}' DELETE s");
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
        var encodedNode = Key(nodeId.Value);
        var direction = query.Direction switch
        {
            CodeGraphDirection.Outgoing => $"s.sourceId = '{encodedNode}'",
            CodeGraphDirection.Incoming => $"s.targetId = '{encodedNode}'",
            CodeGraphDirection.Both => $"(s.sourceId = '{encodedNode}' OR s.targetId = '{encodedNode}')",
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        var kinds = query.EdgeKinds.Count == 0
            ? string.Empty
            : " AND (" + string.Join(" OR ", query.EdgeKinds.Select(kind => $"s.kind = '{Key(kind.Value)}'")) + ")";
        var evidence = query.EvidenceKinds.Count == 0
            ? string.Empty
            : " AND (" + string.Join(" OR ", query.EvidenceKinds.Select(kind => $"s.evidenceKind = {(int)kind}")) + ")";
        return ReadMany<CodeGraphEdge>(
                "HetuEdge",
                $"s.repositoryId = '{Key(repositoryId.Value)}' AND {direction}{kinds}{evidence}")
            .GroupBy(edge => edge.Id)
            .Select(group => group.First())
            .OrderBy(edge => edge.Kind.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id.Value, StringComparer.Ordinal)
            .ToArray();
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
    private static string UnitKey(CodeRepositoryId repositoryId, CodePluginId pluginId, CodeIndexUnitId unitId) =>
        Key($"{repositoryId.Value}\n{pluginId.Value}\n{unitId.Value}");

    private static List<PersistedCommand> Apply(IReadOnlyList<PersistedCommand> current, PersistedCommand command)
    {
        var next = current.Where(existing => !SameSlot(existing, command)).ToList();
        if (command.Kind != "delete")
            next.Add(command);
        return next;
    }

    private static bool SameSlot(PersistedCommand existing, PersistedCommand command) => command.Kind switch
    {
        "repository" => existing.Kind == "repository" && existing.Repository!.Id == command.Repository!.Id,
        "run" or "complete" => existing.Kind is "run" or "complete" && existing.Run!.RepositoryId == command.Run!.RepositoryId && existing.Run.Id == command.Run.Id,
        "replace" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.Replacement!.Origin),
        "delete" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!),
        _ => false
    };

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
            await store.ReplaceIndexUnitAsync(command.Replacement!, cancellationToken);
        }
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
            case "replace": await store.ReplaceIndexUnitAsync(command.Replacement!, cancellationToken); break;
            case "delete": await store.DeleteIndexUnitAsync(command.RepositoryId!, command.PluginId!, command.UnitId!, cancellationToken); break;
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
