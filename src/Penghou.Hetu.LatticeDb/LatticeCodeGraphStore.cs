using System.Text;
using System.Text.Json;
using LatticeDbSharp;
using Penghou.Hetu.LatticeDb;
using static Penghou.Hetu.DurableCommandLog;

namespace Penghou.Hetu;

/// <summary>Durable embedded Hetu store backed by LatticeDB.</summary>
/// <remarks>
/// <para>
/// LatticeDB is the durable command log; all graph semantics and query behavior
/// are served from a materialized <see cref="InMemoryCodeGraphStore"/> rebuilt by
/// replaying that log on open. When <see cref="LatticeDb.LatticeDbStoreOptions.UseNativeTraversal"/>
/// is enabled, an experimental native mirror additionally serves
/// <c>TraverseAsync</c> without evidence filters from real graph structure;
/// everything else keeps delegating to the inner store.
/// </para>
/// <para>
/// Ownership and errors: a database file has a single owner process while open.
/// Opening a second store on the same path (in- or out-of-process) fails rather
/// than queuing. Native failures surface as <see cref="Hetu.CodeGraphStoreException"/>
/// with the engine error preserved as the inner exception; Hetu validation
/// failures keep their <see cref="Hetu.CodeGraphBatchRejectedException"/> contract.
/// </para>
/// <para>
/// Schema evolution: <see cref="CurrentSchemaVersion"/> is validated on open and
/// mismatches are rejected; see the repository ROADMAP for the migration policy
/// (export/import; no in-place upgrades in preview).
/// </para>
/// </remarks>
public sealed class LatticeCodeGraphStore :
    ICodeGraphStore,
    ICodeGraphStoreHealthCheck,
    IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private const string MetadataLabel = "HetuMetadata";
    private const string RepositoryLabel = "HetuRepository";
    private const string RunLabel = "HetuRun";
    private const string IndexStateLabel = "HetuIndexState";
    private const string UnitLabel = "HetuUnit";
    private const string StageLabel = "HetuStage";
    private const string BaselineLabel = "HetuBaseline";
    private const string SchemaKey = "schema";

    private readonly LatticeDatabase _database;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _nativeTraversal;
    private List<PersistedCommand> _commands;
    private readonly Dictionary<string, string?> _baselines = new(StringComparer.Ordinal);
    private volatile InMemoryCodeGraphStore _inner;
    private volatile Dictionary<string, NativeGraphMirror> _mirrors =
        new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly Action<string>? _faultInjector;

    public LatticeCodeGraphStore(string databasePath)
        : this(databasePath, null, null)
    {
    }

    public LatticeCodeGraphStore(string databasePath, LatticeDbStoreOptions? options)
        : this(databasePath, options, null)
    {
    }

    internal LatticeCodeGraphStore(
        string databasePath,
        LatticeDbStoreOptions? options,
        Action<string>? faultInjector)
        : this(databasePath, options, faultInjector, CancellationToken.None)
    {
    }

    private LatticeCodeGraphStore(
        string databasePath,
        LatticeDbStoreOptions? options,
        Action<string>? faultInjector,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A LatticeDB database file path is required.", nameof(databasePath));
        _database = TranslateNative(
            () => LatticeDatabase.Open(databasePath, ToNativeOptions(options)),
            "open database");
        _nativeTraversal = options?.UseNativeTraversal is true;
        _faultInjector = faultInjector;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeSchema();
            cancellationToken.ThrowIfCancellationRequested();
            var repositories = ReadTable<CodeRepositoryManifest>(RepositoryLabel);
            var runs = ReadTable<CodeIndexRunManifest>(RunLabel);
            var units = ReadTable<CodeIndexUnitReplacement>(UnitLabel);
            var stages = ReadTable<PersistedCommand>(StageLabel);
            var states = ReadTable<CodeRepositoryIndexState>(IndexStateLabel);
            _commands = Reconstruct(repositories, runs, units, stages, states);
            _inner = ReplayAsync(_commands, cancellationToken).GetAwaiter().GetResult();
            foreach (var (key, baseline) in ReadPairs(BaselineLabel))
            {
                var separator = key.IndexOf('\n');
                if (separator <= 0 || separator == key.Length - 1)
                    throw new InvalidDataException($"Lattice {BaselineLabel} key is invalid.");
                _baselines.Add(key, string.IsNullOrEmpty(baseline) ? null : baseline);
            }
            RestoreBaselines(_inner);
            cancellationToken.ThrowIfCancellationRequested();
            if (_nativeTraversal)
            {
                RestoreMirrors(repositories.Select(repository => repository.Id.Value).ToArray());
            }
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    /// <summary>Opens a store without blocking the calling thread on native I/O.</summary>
    /// <remarks>Construction performs native I/O and full log replay, so this factory
    /// offloads that work to the thread pool; the returned store is identical to the
    /// constructor overloads.</remarks>
    public static Task<LatticeCodeGraphStore> OpenAsync(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        OpenAsync(databasePath, null, cancellationToken);

    /// <summary>Opens a tuned store without blocking the calling thread on native I/O.</summary>
    public static Task<LatticeCodeGraphStore> OpenAsync(
        string databasePath,
        LatticeDbStoreOptions? options,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new LatticeCodeGraphStore(databasePath, options, null, cancellationToken);
        }, cancellationToken);

    public ValueTask UpsertRepositoryAsync(CodeRepositoryManifest repository, CancellationToken cancellationToken = default) =>
        MutateAsync(new("repository", Repository: repository), cancellationToken);

    public ValueTask<CodeRepositoryManifest?> GetRepositoryAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetRepositoryAsync(repositoryId, cancellationToken), cancellationToken);

    public ValueTask StoreIndexRunAsync(CodeIndexRunManifest run, CancellationToken cancellationToken = default) =>
        MutateAsync(new("run", Run: run), cancellationToken);

    public ValueTask<CodeIndexRunManifest?> GetIndexRunAsync(CodeRepositoryId repositoryId, CodeIndexRunId runId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetIndexRunAsync(repositoryId, runId, cancellationToken), cancellationToken);

    public ValueTask CompleteIndexRunAsync(CodeIndexRunManifest completedRun, CodeRepositoryIndexState state, CancellationToken cancellationToken = default) =>
        MutateAsync(new("complete", Run: completedRun, State: state), cancellationToken);

    public ValueTask<CodeRepositoryIndexState?> GetLatestIndexStateAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetLatestIndexStateAsync(repositoryId, cancellationToken), cancellationToken);

    public ValueTask<CodeGraphPublication?> GetLatestPublicationAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetLatestPublicationAsync(repositoryId, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<CodeIndexUnitReplacement>> GetPublishedUnitsAsync(CodeRepositoryId repositoryId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetPublishedUnitsAsync(repositoryId, cancellationToken), cancellationToken);

    public ValueTask StageIndexUnitAsync(CodeIndexUnitReplacement replacement, CancellationToken cancellationToken = default) =>
        MutateAsync(new("stage-replace", Replacement: replacement), cancellationToken);

    public ValueTask StageIndexUnitDeletionAsync(CodeRepositoryId repositoryId, CodeIndexRunId indexRunId, CodePluginId pluginId, CodeIndexUnitId indexUnitId, CancellationToken cancellationToken = default) =>
        MutateAsync(new("stage-delete", RepositoryId: repositoryId, RunId: indexRunId, PluginId: pluginId, UnitId: indexUnitId), cancellationToken);

    public ValueTask<CodeGraphNode?> GetNodeAsync(CodeRepositoryId repositoryId, CodeNodeId nodeId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetNodeAsync(repositoryId, nodeId, cancellationToken), cancellationToken);

    public ValueTask<CodeGraphNode?> FindSymbolAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.FindSymbolAsync(repositoryId, symbolId, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<CodeGraphNode>> FindNodesByQualifiedNameAsync(CodeRepositoryId repositoryId, string qualifiedName, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.FindNodesByQualifiedNameAsync(repositoryId, qualifiedName, cancellationToken), cancellationToken);

    public ValueTask<CodeNamePatternResult> FindNodesByNamePatternAsync(
        CodeRepositoryId repositoryId,
        string pattern,
        int maxResults = CodeNamePatternResult.DefaultMaxResults,
        CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.FindNodesByNamePatternAsync(repositoryId, pattern, maxResults, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsAsync(CodeRepositoryId repositoryId, CodeSymbolId symbolId, CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetDeclarationsAsync(repositoryId, symbolId, cancellationToken), cancellationToken);

    public async ValueTask<CodeGraphTraversalResult> TraverseAsync(CodeRepositoryId repositoryId, CodeGraphTraversalQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        ArgumentNullException.ThrowIfNull(query);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeTraversal &&
                query.EvidenceKinds.Count == 0 &&
                _mirrors.TryGetValue(repositoryId.Value, out var mirror))
            {
                return TranslateNative(() =>
                {
                    using var txn = _database.BeginReadTransaction();
                    var result = mirror.Traverse(txn, query, cancellationToken);
                    txn.Commit();
                    return result;
                }, "traverse graph");
            }

            return await _inner.TraverseAsync(repositoryId, query, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?> FindNodesByQualifiedNameWithProvenanceAsync(
        CodeRepositoryId repositoryId,
        string qualifiedName,
        CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.FindNodesByQualifiedNameWithProvenanceAsync(repositoryId, qualifiedName, cancellationToken), cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?> TraverseWithProvenanceAsync(
        CodeRepositoryId repositoryId,
        CodeGraphTraversalQuery query,
        CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.TraverseWithProvenanceAsync(repositoryId, query, cancellationToken), cancellationToken);

    public ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?> GetDeclarationsWithProvenanceAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default) =>
        ReadAsync(store => store.GetDeclarationsWithProvenanceAsync(repositoryId, symbolId, cancellationToken), cancellationToken);

    private CodeGraphStoreHealth CheckHealth()
    {
        ThrowIfDisposed();
        var version = ReadSchemaVersion();
        var healthy = version == CurrentSchemaVersion;
        return new CodeGraphStoreHealth(
            healthy ? CodeGraphStoreHealthStatus.Healthy : CodeGraphStoreHealthStatus.Unhealthy,
            "lattice",
            healthy
                ? _nativeTraversal
                    ? "Native traversal mirrors are enabled and verified against the current publication on open."
                    : "Queries use the authoritative materialized projection; native traversal mirrors are disabled."
                : $"Schema version {version} is incompatible with expected version {CurrentSchemaVersion}.",
            version,
            Count(RepositoryLabel),
            Count(RunLabel),
            Count(UnitLabel));
    }

    public async ValueTask<CodeGraphStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return CheckHealth();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CodeGraphStoreHealth(
                CodeGraphStoreHealthStatus.Unhealthy,
                "lattice",
                exception.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _writeGate.Wait();
        try
        {
            if (_disposed)
                return;
            _database.Dispose();
            _disposed = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask MutateAsync(PersistedCommand command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var runKey = command.Run is null ? null : RunKey(command.Run);
            string? baseline = null;
            if (command is { Kind: "run", Run.Status: CodeIndexRunStatus.Running })
            {
                if (!_baselines.TryGetValue(runKey!, out baseline))
                {
                    baseline = (await _inner.GetLatestPublicationAsync(command.Run.RepositoryId, cancellationToken)
                        .ConfigureAwait(false))?.IndexRunId.Value;
                }
            }
            var next = Apply(_commands, command);
            await ApplyCommandAsync(_inner, command, cancellationToken).ConfigureAwait(false);
            try
            {
                var mirror = TranslateNative(() => Persist(command, baseline, cancellationToken), "persist mutation");
                _commands = next;
                if (command is { Kind: "run", Run.Status: CodeIndexRunStatus.Running })
                    _baselines.TryAdd(runKey!, baseline);
                else if (command.Kind == "complete" ||
                         command is { Kind: "run", Run.Status: not CodeIndexRunStatus.Running })
                    _baselines.Remove(runKey!);

                if (command.Kind == "complete" && command.Run is not null)
                {
                    var mirrors = new Dictionary<string, NativeGraphMirror>(_mirrors, StringComparer.Ordinal);
                    if (mirror is null)
                        mirrors.Remove(command.Run.RepositoryId.Value);
                    else
                        mirrors[command.Run.RepositoryId.Value] = mirror;
                    _mirrors = mirrors;
                }
            }
            catch
            {
                _inner = await ReplayAsync(_commands, CancellationToken.None).ConfigureAwait(false);
                RestoreBaselines(_inner);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<T> ReadAsync<T>(
        Func<InMemoryCodeGraphStore, ValueTask<T>> read,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await read(_inner).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RestoreBaselines(InMemoryCodeGraphStore store)
    {
        foreach (var (key, baseline) in _baselines)
        {
            var separator = key.IndexOf('\n');
            store.RestoreBaseline(key[..separator], key[(separator + 1)..], baseline);
        }
    }

    private void RestoreMirrors(string[] repositoryIds)
    {
        var mirrors = new Dictionary<string, NativeGraphMirror>(StringComparer.Ordinal);
        var existingMirrors = new Dictionary<string, NativeGraphMirror>(StringComparer.Ordinal);
        foreach (var (key, payload) in ReadPairs(NativeGraphMirror.MirrorLabel))
        {
            try
            {
                existingMirrors[key] = new NativeGraphMirror(NativeGraphMirror.ReadManifest(
                    JsonSerializer.Deserialize<string>(
                        Convert.FromBase64String(payload), SerializerOptions)
                    ?? throw new InvalidDataException()));
            }
            catch (Exception exception) when (exception is FormatException or JsonException or InvalidDataException)
            {
                // Derived mirror metadata is recoverable. Rebuild it from the
                // authoritative published units below.
            }
        }

        foreach (var repositoryId in repositoryIds)
        {
            var repository = new CodeRepositoryId(repositoryId);
            var publication = _inner.GetLatestPublicationAsync(repository, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (publication is null)
                continue;
            var units = _inner.GetPublishedUnitsAsync(repository, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var txn = _database.BeginWriteTransaction();
            try
            {
                existingMirrors.TryGetValue(repositoryId, out var existing);
                var mirror = TranslateNative(
                    () => NativeGraphMirror.Rebuild(txn, units, existing?.Manifest),
                    "rebuild graph mirror");
                Upsert(txn, NativeGraphMirror.MirrorLabel, repositoryId, Serialize(NativeGraphMirror.WriteManifest(mirror.Manifest)));
                txn.Commit();
                mirrors[repositoryId] = mirror;
            }
            catch
            {
                if (!txn.IsCompleted)
                    txn.Rollback();
                throw;
            }
        }

        _mirrors = mirrors;
    }

    private NativeGraphMirror? Persist(PersistedCommand command, string? baseline, CancellationToken cancellationToken)
    {
        NativeGraphMirror? mirror = null;
        cancellationToken.ThrowIfCancellationRequested();
        using var txn = _database.BeginWriteTransaction();
        try
        {
            switch (command.Kind)
            {
                case "repository":
                    Upsert(txn, RepositoryLabel, command.Repository!.Id.Value, Serialize(command.Repository));
                    break;
                case "run":
                    Upsert(txn, RunLabel, RunKey(command.Run!), Serialize(command.Run));
                    if (command.Run!.Status != CodeIndexRunStatus.Running)
                    {
                        DeleteStagedRun(txn, command.Run);
                        DeleteMany(txn, BaselineLabel, [RunKey(command.Run)]);
                    }
                    else
                    {
                        Upsert(txn, BaselineLabel, RunKey(command.Run), baseline ?? string.Empty);
                    }
                    break;
                case "complete":
                    PublishStagedRun(txn, command.Run!);
                    Upsert(txn, RunLabel, RunKey(command.Run!), Serialize(command.Run));
                    Upsert(txn, IndexStateLabel, command.State!.RepositoryId.Value, Serialize(command.State));
                    DeleteMany(txn, BaselineLabel, [RunKey(command.Run!)]);
                    if (_nativeTraversal)
                    {
                        var completed = command.Run!;
                        var repositoryId = completed.RepositoryId.Value;
                        var published = Apply(_commands, command)
                            .Where(candidate => candidate.Kind == "replace" &&
                                candidate.Replacement!.Origin.RepositoryId == completed.RepositoryId)
                            .Select(candidate => candidate.Replacement!)
                            .ToList();
                        _mirrors.TryGetValue(repositoryId, out var existing);
                        mirror = NativeGraphMirror.Rebuild(txn, published, existing?.Manifest);
                        Upsert(
                            txn,
                            NativeGraphMirror.MirrorLabel,
                            repositoryId,
                            Serialize(NativeGraphMirror.WriteManifest(mirror.Manifest)));
                    }
                    else
                    {
                        DeleteMany(txn, NativeGraphMirror.MirrorLabel, [command.Run!.RepositoryId.Value]);
                    }

                    break;
                case "stage-replace":
                case "stage-delete":
                    Upsert(txn, StageLabel, StageKey(command), Serialize(command));
                    break;
                case "replace":
                    Upsert(txn, UnitLabel, UnitKey(command.Replacement!.Origin), Serialize(command.Replacement));
                    break;
                case "delete":
                    DeleteMany(txn, UnitLabel, [UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!)]);
                    break;
                default: throw new InvalidDataException($"Unknown persisted Hetu command '{command.Kind}'.");
            }
            _faultInjector?.Invoke("before-commit");
            txn.Commit();
            return mirror;
        }
        catch
        {
            if (!txn.IsCompleted)
                txn.Rollback();
            throw;
        }
    }

    private void PublishStagedRun(LatticeTransaction txn, CodeIndexRunManifest run)
    {
        var staged = _commands.Where(command => MatchesRun(command, run)).ToArray();
        var unitUpserts = staged
            .Where(command => command.Kind == "stage-replace")
            .Select(command => (UnitKey(command.Replacement!.Origin), Serialize(command.Replacement)))
            .ToArray();
        var unitDeletions = staged
            .Where(command => command.Kind == "stage-delete")
            .Select(command => UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!))
            .ToArray();
        UpsertMany(txn, UnitLabel, unitUpserts);
        DeleteMany(txn, UnitLabel, unitDeletions);
        DeleteMany(txn, StageLabel, staged.Select(StageKey).ToArray());
    }

    private void DeleteStagedRun(LatticeTransaction txn, CodeIndexRunManifest run)
    {
        DeleteMany(
            txn,
            StageLabel,
            _commands.Where(command => MatchesRun(command, run)).Select(StageKey).ToArray());
    }

    private void Upsert(LatticeTransaction txn, string label, string key, string payload) =>
        UpsertMany(txn, label, [(key, payload)]);

    private void UpsertMany(
        LatticeTransaction txn,
        string label,
        IReadOnlyList<(string Key, string Payload)> records)
    {
        if (records.Count == 0)
            return;
        DeleteMany(txn, label, records.Select(record => record.Key).ToArray());
        var statement = new StringBuilder();
        for (var index = 0; index < records.Count; index++)
            statement.Append($"CREATE (:{label} {{key: $k{index}, payload: $p{index}}}) ");
        using var query = _database.Prepare(statement.ToString());
        for (var index = 0; index < records.Count; index++)
        {
            query.Bind($"k{index}", LatticeValue.From(records[index].Key));
            query.Bind($"p{index}", LatticeValue.From(records[index].Payload));
        }
        using var result = query.Execute(txn);
        result.ReadAll();
    }

    private void DeleteMany(LatticeTransaction txn, string label, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return;
        using var query = _database.Prepare($"MATCH (s:{label}) WHERE s.key IN $keys DELETE s");
        query.Bind("keys", LatticeValue.From(keys.Select(LatticeValue.From).ToArray()));
        using var result = query.Execute(txn);
        result.ReadAll();
    }

    private void InitializeSchema() =>
        TranslateNative(() => InitializeSchemaCore(), "initialize schema");

    private void InitializeSchemaCore()
    {
        var existing = ReadMetadata();
        if (existing is null)
        {
            using var txn = _database.BeginWriteTransaction();
            try
            {
                var node = txn.CreateNode(MetadataLabel);
                txn.SetProperty(node, "key", LatticeValue.From(SchemaKey));
                txn.SetProperty(node, "schemaVersion", LatticeValue.From((long)CurrentSchemaVersion));
                txn.Commit();
            }
            catch
            {
                if (!txn.IsCompleted)
                    txn.Rollback();
                throw;
            }
            return;
        }

        if (existing.Value != CurrentSchemaVersion)
            throw new LatticeCodeGraphSchemaException(existing.Value, CurrentSchemaVersion);
    }

    private int? ReadMetadata() =>
        TranslateNative(() => ReadMetadataCore(), "read metadata");

    private int? ReadMetadataCore()
    {
        using var txn = _database.BeginReadTransaction();
        using var query = _database.Prepare($"MATCH (s:{MetadataLabel}) WHERE s.key = $key RETURN s.schemaVersion");
        query.Bind("key", LatticeValue.From(SchemaKey));
        using var result = query.Execute(txn);
        var found = result.MoveNext();
        var version = found ? (int)result.Current.GetValue(0).AsInt64() : (int?)null;
        txn.Commit();
        return version;
    }

    private int ReadSchemaVersion() => ReadMetadata() ?? CurrentSchemaVersion;

    private static T TranslateNative<T>(Func<T> action, string operation)
    {
        try
        {
            return action();
        }
        catch (LatticeException exception)
        {
            throw new CodeGraphStoreException(
                $"LatticeDB {operation} failed.",
                "lattice",
                exception.NativeErrorCode.ToString(),
                exception);
        }
    }

    private static void TranslateNative(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (LatticeException exception)
        {
            throw new CodeGraphStoreException(
                $"LatticeDB {operation} failed.",
                "lattice",
                exception.NativeErrorCode.ToString(),
                exception);
        }
    }

    private List<(string Key, string Payload)> ReadPairs(string label) =>
        TranslateNative(() => ReadPairsCore(label), "read records");

    private List<(string Key, string Payload)> ReadPairsCore(string label)
    {
        IReadOnlyList<LatticeRow> rows;
        using (var txn = _database.BeginReadTransaction())
        {
            using var query = _database.Prepare($"MATCH (s:{label}) RETURN s.key, s.payload");
            using var result = query.Execute(txn);
            rows = result.ReadAll();
            txn.Commit();
        }
        return rows
            .Select(row => (row.GetValue(0).AsString(), row.GetValue(1).AsString()))
            .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToList();
    }

    private List<T> ReadTable<T>(string label) =>
        TranslateNative(() => ReadTableCore<T>(label), "read records");

    private List<T> ReadTableCore<T>(string label)
    {
        IReadOnlyList<LatticeRow> rows;
        using (var txn = _database.BeginReadTransaction())
        {
            using var query = _database.Prepare($"MATCH (s:{label}) RETURN s.key, s.payload");
            using var result = query.Execute(txn);
            rows = result.ReadAll();
            txn.Commit();
        }
        return rows
            .Select(row => (
                Key: row.GetValue(0).AsString(),
                Value: JsonSerializer.Deserialize<T>(
                    Convert.FromBase64String(row.GetValue(1).AsString()), SerializerOptions)
                    ?? throw new InvalidDataException($"Lattice {label} payload is invalid.")))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToList();
    }

    private int Count(string label) =>
        TranslateNative(() => CountCore(label), "count records");

    private int CountCore(string label)
    {
        using var txn = _database.BeginReadTransaction();
        using var query = _database.Prepare($"MATCH (s:{label}) RETURN count(s)");
        using var result = query.Execute(txn);
        var rows = result.ReadAll();
        txn.Commit();
        return rows.Count == 0 ? 0 : (int)rows[0].GetValue(0).AsInt64();
    }

    private static LatticeDbSharp.LatticeDatabaseOptions ToNativeOptions(LatticeDbStoreOptions? options)
    {
        if (options is null)
            return new LatticeDbSharp.LatticeDatabaseOptions { Create = true };
        var defaults = new LatticeDbSharp.LatticeDatabaseOptions();
        return new LatticeDbSharp.LatticeDatabaseOptions
        {
            Create = true,
            CacheSizeMb = options.CacheSizeMb ?? defaults.CacheSizeMb,
            PageSize = options.PageSize ?? defaults.PageSize,
            EnableWal = options.EnableWal ?? defaults.EnableWal,
            EnableAdjacencyCache = options.EnableAdjacencyCache ?? defaults.EnableAdjacencyCache
        };
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class LatticeCodeGraphSchemaException : Exception
{
    public LatticeCodeGraphSchemaException(int actualVersion, int expectedVersion)
        : base($"Lattice Hetu schema version {actualVersion} is incompatible with expected version {expectedVersion}.")
    {
        ActualVersion = actualVersion;
        ExpectedVersion = expectedVersion;
    }

    public int ActualVersion { get; }
    public int ExpectedVersion { get; }
}

