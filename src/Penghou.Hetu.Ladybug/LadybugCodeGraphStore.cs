using System.Text.Json;
using System.Text.Json.Serialization;
using LadybugDB;

namespace Penghou.Hetu;

/// <summary>Durable embedded Hetu store backed by LadybugDB.</summary>
public sealed class LadybugCodeGraphStore : ICodeGraphStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly Database _database;
    private readonly Connection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PersistedCommand> _commands;
    private InMemoryCodeGraphStore _inner;
    private bool _disposed;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public LadybugCodeGraphStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A Ladybug database path is required.", nameof(databasePath));
        _database = new Database(databasePath);
        _connection = new Connection(_database);
        Execute("CREATE NODE TABLE IF NOT EXISTS HetuState(id INT64, schemaVersion INT64, payload STRING, PRIMARY KEY(id))");
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

    public ValueTask<CodeGraphNode?> FindSymbolAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default) =>
        _inner.FindSymbolAsync(repositoryId, symbolId, cancellationToken);

    public ValueTask<IReadOnlyList<CodeGraphNode>> FindNodesByQualifiedNameAsync(CodeRepositoryId repositoryId, string qualifiedName, CancellationToken cancellationToken = default) =>
        _inner.FindNodesByQualifiedNameAsync(repositoryId, qualifiedName, cancellationToken);

    public ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default) =>
        _inner.GetDeclarationsAsync(repositoryId, symbolId, cancellationToken);

    public ValueTask<CodeGraphTraversalResult> TraverseAsync(CodeRepositoryId repositoryId, CodeGraphTraversalQuery query, CancellationToken cancellationToken = default) =>
        _inner.TraverseAsync(repositoryId, query, cancellationToken);

    public LadybugCodeGraphStoreHealth CheckHealth()
    {
        ThrowIfDisposed();
        using var result = _connection.Query("MATCH (s:HetuState) RETURN s.schemaVersion LIMIT 1");
        var row = result.Rows().FirstOrDefault();
        var version = row is null
            ? CurrentSchemaVersion
            : ParseVersion(row[0]?.ToString());
        return new(version == CurrentSchemaVersion, version, _commands.Count);
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
            var next = new List<PersistedCommand>(_commands) { command };
            var candidate = await ReplayAsync(next, cancellationToken).ConfigureAwait(false);
            Persist(next);
            _commands = next;
            _inner = candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Persist(IReadOnlyList<PersistedCommand> commands)
    {
        var payload = Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(commands, SerializerOptions));
        Execute("BEGIN TRANSACTION");
        try
        {
            Execute("MATCH (s:HetuState) WHERE s.id = 1 DELETE s");
            Execute($"CREATE (:HetuState {{id: 1, schemaVersion: {CurrentSchemaVersion}, payload: '{payload}'}})");
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
        using var result = _connection.Query("MATCH (s:HetuState) RETURN s.schemaVersion, s.payload LIMIT 1");
        var row = result.Rows().FirstOrDefault();
        if (row is null)
            return [];
        var version = ParseVersion(row[0]?.ToString());
        if (version != CurrentSchemaVersion)
            throw new LadybugCodeGraphSchemaException(version, CurrentSchemaVersion);
        var payload = row[1]?.ToString() ??
            throw new InvalidDataException("Ladybug Hetu state payload is missing.");
        return JsonSerializer.Deserialize<List<PersistedCommand>>(
            Convert.FromBase64String(payload),
            SerializerOptions) ?? [];
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
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        return store;
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

public sealed record LadybugCodeGraphStoreHealth(bool IsHealthy, int SchemaVersion, int PersistedCommandCount);

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
