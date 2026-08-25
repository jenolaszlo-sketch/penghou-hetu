using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Penghou.Hetu;

/// <summary>Bounds and configures one repository indexing lifecycle.</summary>
public sealed record CodeIndexingOptions
{
    public CodeIndexingOptions(
        CodeIndexPlanningOptions? planning = null,
        CodeGraphBatchLimits? batchLimits = null,
        int maxConcurrentPlugins = 1,
        long maxSourceBytes = 16 * 1024 * 1024,
        long maxTotalSourceBytes = 512 * 1024 * 1024)
    {
        if (maxConcurrentPlugins < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentPlugins));
        if (maxSourceBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSourceBytes));
        if (maxTotalSourceBytes < maxSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTotalSourceBytes));
        Planning = planning ?? new CodeIndexPlanningOptions();
        BatchLimits = batchLimits ?? new CodeGraphBatchLimits();
        MaxConcurrentPlugins = maxConcurrentPlugins;
        MaxSourceBytes = maxSourceBytes;
        MaxTotalSourceBytes = maxTotalSourceBytes;
    }

    public CodeIndexPlanningOptions Planning { get; }
    public CodeGraphBatchLimits BatchLimits { get; }
    public int MaxConcurrentPlugins { get; }
    public long MaxSourceBytes { get; }
    public long MaxTotalSourceBytes { get; }
}

/// <summary>Privacy-safe measurements for one indexing attempt.</summary>
public sealed record CodeIndexingDiagnostics(
    CodeRepositoryId RepositoryId,
    CodeIndexRunId IndexRunId,
    CodeIndexRunStatus Status,
    int FilesDiscovered,
    int FilesNew,
    int FilesChanged,
    int FilesUnchanged,
    int FilesDeleted,
    int FilesUnsupported,
    int DirectoriesExcluded,
    int DirectoriesDepthLimited,
    int ReparsePointsSkipped,
    long SourceBytesRead,
    int PluginsExecuted,
    int IndexUnitsCompleted,
    int IndexUnitsDeleted,
    int NodesProduced,
    int DeclarationsProduced,
    int EdgesProduced,
    int RejectedFacts,
    TimeSpan PlanningDuration,
    TimeSpan ExtractionDuration,
    TimeSpan PersistenceDuration,
    IReadOnlyList<string> WarningCodes);

/// <summary>Result of one completed repository indexing lifecycle.</summary>
public sealed record CodeIndexingResult(CodeIndexPlan Plan, CodeIndexingDiagnostics Diagnostics);

/// <summary>Coordinates repository discovery, plugins, ingestion, and durable run state.</summary>
public sealed class CodeIndexingService
{
    private readonly CodeRepositoryProviderRegistry _repositories;
    private readonly CodeGraphPluginRegistry _plugins;
    private readonly ICodeGraphStore _store;
    private readonly TimeProvider _timeProvider;

    public CodeIndexingService(
        CodeRepositoryProviderRegistry repositories,
        CodeGraphPluginRegistry plugins,
        ICodeGraphStore store,
        TimeProvider? timeProvider = null)
    {
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CodeIndexingResult> IndexAsync(
        CodeRepositoryDescriptor descriptor,
        CodeIndexRunId runId,
        CodeIndexingOptions? options = null,
        Action<CodeIndexingDiagnostics>? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(runId);
        options ??= new CodeIndexingOptions();
        var startedAt = _timeProvider.GetUtcNow();
        CodeIndexPlan plan;
        var ingestion = new ConcurrentBag<CodeGraphIngestionDiagnostics>();
        var extractionDuration = TimeSpan.Zero;
        var persistenceDuration = TimeSpan.Zero;
        var pluginsExecuted = 0;
        var unitsDeleted = 0;
        long sourceBytesRead = 0;

        await using var repository = await _repositories.OpenAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        var existingRepository = await _store.GetRepositoryAsync(descriptor.Id, cancellationToken)
            .ConfigureAwait(false);
        await _store.UpsertRepositoryAsync(
            new CodeRepositoryManifest(
                descriptor.Id,
                existingRepository?.DisplayName,
                descriptor.Location,
                existingRepository?.RegisteredAt),
            cancellationToken).ConfigureAwait(false);

        var previousState = await _store.GetLatestIndexStateAsync(descriptor.Id, cancellationToken)
            .ConfigureAwait(false);
        var planner = new CodeIndexPlanner(_plugins);
        var planningStarted = Stopwatch.GetTimestamp();
        var planningOptions = new CodeIndexPlanningOptions(
            options.Planning.Enumeration,
            options.Planning.PluginIds,
            options.MaxSourceBytes,
            options.MaxTotalSourceBytes);
        plan = await planner.CreatePlanAsync(
            repository,
            previousState?.Sources,
            planningOptions,
            cancellationToken).ConfigureAwait(false);
        var planningDuration = Stopwatch.GetElapsedTime(planningStarted);
        var selectedPlugins = SelectPlugins(plan, options.Planning);
        var executingPlugins = selectedPlugins
            .Where(plugin => plan.Items.Any(item =>
                item.Manifest.PluginId == plugin.Id &&
                item.Status != CodeIndexPlanStatus.Unchanged))
            .ToArray();
        var running = new CodeIndexRunManifest(
            descriptor.Id,
            runId,
            startedAt,
            plugins: executingPlugins.Select(plugin => plugin.Id).ToArray());
        await _store.StoreIndexRunAsync(running, cancellationToken).ConfigureAwait(false);

        try
        {
            var extractionStarted = Stopwatch.GetTimestamp();
            var materialized = await MaterializeSourcesAsync(
                repository,
                plan,
                executingPlugins,
                options,
                cancellationToken).ConfigureAwait(false);
            sourceBytesRead = plan.HashBytesRead +
                materialized.Values.Sum(source => (long)source.Content.Length);
            await using var sink = new CodeGraphIngestionSink(
                _store,
                options.BatchLimits,
                onCompleted: ingestion.Add);
            using var concurrency = new SemaphoreSlim(options.MaxConcurrentPlugins);
            var results = new ConcurrentBag<(ICodeGraphPlugin Plugin, CodeGraphExtractionResult Result)>();
            var tasks = executingPlugins.Select(async plugin =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var context = CreateContext(
                        descriptor, runId, plugin, plan, materialized, previousState);
                    await using var session = await plugin.CreateSessionAsync(context, cancellationToken)
                        .ConfigureAwait(false);
                    var scopedSink = new PluginScopedSink(
                        sink, descriptor.Id, runId, plugin.Id, plugin.Version);
                    var result = await session.ExtractAsync(scopedSink, cancellationToken)
                        .ConfigureAwait(false);
                    results.Add((plugin, result ?? throw new InvalidOperationException(
                        $"Plugin '{plugin.Id}' returned a null extraction result.")));
                    Interlocked.Increment(ref pluginsExecuted);
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            extractionDuration = Stopwatch.GetElapsedTime(extractionStarted);

            var persistenceStarted = Stopwatch.GetTimestamp();
            foreach (var (plugin, result) in results.OrderBy(result => result.Plugin.Id.Value, StringComparer.Ordinal))
            {
                foreach (var unitId in result.ObsoleteIndexUnits)
                {
                    await _store.DeleteIndexUnitAsync(
                        descriptor.Id,
                        plugin.Id,
                        unitId,
                        cancellationToken).ConfigureAwait(false);
                    unitsDeleted++;
                }
            }

            var completedAt = _timeProvider.GetUtcNow();
            var completed = new CodeIndexRunManifest(
                descriptor.Id,
                runId,
                startedAt,
                CodeIndexRunStatus.Completed,
                completedAt,
                executingPlugins.Select(plugin => plugin.Id).ToArray());
            var state = CreateState(descriptor.Id, runId, repository, previousState, plan, selectedPlugins);
            await _store.CompleteIndexRunAsync(completed, state, cancellationToken).ConfigureAwait(false);
            persistenceDuration = Stopwatch.GetElapsedTime(persistenceStarted);
            var final = CreateDiagnostics(
                descriptor.Id, runId, CodeIndexRunStatus.Completed, plan, ingestion,
                pluginsExecuted, unitsDeleted, sourceBytesRead,
                planningDuration, extractionDuration, persistenceDuration);
            Report(diagnostics, final);
            return new(plan, final);
        }
        catch (Exception exception)
        {
            var status = exception is OperationCanceledException
                ? CodeIndexRunStatus.Cancelled
                : CodeIndexRunStatus.Failed;
            await TryFinishFailedRunAsync(running, status).ConfigureAwait(false);
            var final = CreateDiagnostics(
                descriptor.Id, runId, status, plan, ingestion,
                pluginsExecuted, unitsDeleted, sourceBytesRead,
                planningDuration, extractionDuration, persistenceDuration);
            Report(diagnostics, final);
            throw;
        }
    }

    private IReadOnlyList<ICodeGraphPlugin> SelectPlugins(
        CodeIndexPlan plan,
        CodeIndexPlanningOptions options)
    {
        var ids = options.PluginIds.Count > 0
            ? options.PluginIds.ToHashSet()
            : plan.Items.Select(item => item.Manifest.PluginId).ToHashSet();
        return _plugins.Plugins.Where(plugin => ids.Contains(plugin.Id)).ToArray();
    }

    private static CodeGraphPluginContext CreateContext(
        CodeRepositoryDescriptor descriptor,
        CodeIndexRunId runId,
        ICodeGraphPlugin plugin,
        CodeIndexPlan plan,
        IReadOnlyDictionary<string, MaterializedSource> materialized,
        CodeRepositoryIndexState? previousState)
    {
        var previous = previousState?.Sources
            .Where(source => source.PluginId == plugin.Id)
            .ToDictionary(source => source.SourcePath, StringComparer.Ordinal) ?? [];
        var items = plan.Items.Where(item => item.Manifest.PluginId == plugin.Id).ToArray();
        var sources = items
            .Where(item => item.Source is not null)
            .Select(item =>
            {
                var source = materialized[$"{plugin.Id.Value}\n{item.Manifest.SourcePath}"];
                return new CodeGraphSource(
                    item.Manifest.SourcePath,
                    item.Manifest.SourceHash,
                    cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Stream stream = new MemoryStream(source.Content, writable: false);
                        return new ValueTask<Stream>(stream);
                    });
            })
            .ToArray();
        var changes = items.Select(item => new CodeGraphSourceChange(
            item.Manifest.SourcePath,
            (CodeGraphSourceChangeKind)item.Status,
            previous.GetValueOrDefault(item.Manifest.SourcePath)?.SourceHash,
            item.Status == CodeIndexPlanStatus.Deleted ? null : item.Manifest.SourceHash)).ToArray();
        return new(descriptor.Id, descriptor.Location, runId, sources, descriptor.Settings, changes);
    }

    private static CodeRepositoryIndexState CreateState(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        ICodeRepositorySource repository,
        CodeRepositoryIndexState? previousState,
        CodeIndexPlan plan,
        IReadOnlyList<ICodeGraphPlugin> selectedPlugins)
    {
        var selectedIds = selectedPlugins.Select(plugin => plugin.Id).ToHashSet();
        var retained = previousState?.Sources.Where(source => !selectedIds.Contains(source.PluginId)) ?? [];
        var current = plan.Items
            .Where(item => item.Status != CodeIndexPlanStatus.Deleted)
            .Select(item => item.Manifest);
        return new(
            repositoryId,
            runId,
            retained.Concat(current).ToArray(),
            repository.SnapshotIdentity,
            repository.IsConsistentSnapshot);
    }

    private async ValueTask TryFinishFailedRunAsync(
        CodeIndexRunManifest running,
        CodeIndexRunStatus status)
    {
        try
        {
            await _store.StoreIndexRunAsync(
                new CodeIndexRunManifest(
                    running.RepositoryId,
                    running.Id,
                    running.StartedAt,
                    status,
                    _timeProvider.GetUtcNow(),
                    running.Plugins),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original extraction or cancellation exception remains authoritative.
        }
    }

    private static CodeIndexingDiagnostics CreateDiagnostics(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CodeIndexRunStatus status,
        CodeIndexPlan plan,
        IEnumerable<CodeGraphIngestionDiagnostics> ingestion,
        int pluginsExecuted,
        int unitsDeleted,
        long sourceBytesRead,
        TimeSpan planningDuration,
        TimeSpan extractionDuration,
        TimeSpan persistenceDuration)
    {
        var units = ingestion.ToArray();
        return new(
            repositoryId,
            runId,
            status,
            plan.RepositoryEntries,
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.New),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Changed),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Unchanged),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Deleted),
            plan.UnsupportedEntries,
            plan.ExcludedDirectories,
            plan.DepthLimitedDirectories,
            plan.ReparsePointsSkipped,
            sourceBytesRead,
            pluginsExecuted,
            units.Length,
            unitsDeleted,
            units.Sum(unit => unit.NodesReceived),
            units.Sum(unit => unit.DeclarationsReceived),
            units.Sum(unit => unit.EdgesReceived),
            units.Sum(unit => unit.RejectedFacts),
            planningDuration,
            extractionDuration,
            persistenceDuration,
            units.SelectMany(unit => unit.WarningCodes).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    private static async ValueTask<IReadOnlyDictionary<string, MaterializedSource>> MaterializeSourcesAsync(
        ICodeRepositorySource repository,
        CodeIndexPlan plan,
        IReadOnlyList<ICodeGraphPlugin> plugins,
        CodeIndexingOptions options,
        CancellationToken cancellationToken)
    {
        var pluginIds = plugins.Select(plugin => plugin.Id).ToHashSet();
        var result = new Dictionary<string, MaterializedSource>(StringComparer.Ordinal);
        var totalBytes = plan.HashBytesRead;
        foreach (var item in plan.Items.Where(item =>
                     item.Source is not null && pluginIds.Contains(item.Manifest.PluginId)))
        {
            var entry = item.Source!;
            if (entry.Length > options.MaxSourceBytes)
                throw new CodeSourceSizeLimitException(entry.Path, options.MaxSourceBytes, false);
            await using var input = await repository.OpenReadAsync(entry, cancellationToken)
                .ConfigureAwait(false);
            await using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                totalBytes += read;
                if (output.Length + read > options.MaxSourceBytes)
                    throw new CodeSourceSizeLimitException(entry.Path, options.MaxSourceBytes, false);
                if (totalBytes > options.MaxTotalSourceBytes)
                    throw new CodeSourceSizeLimitException(entry.Path, options.MaxTotalSourceBytes, true);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            var content = output.ToArray();
            if (item.Manifest.SourceHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var actual = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
                if (!string.Equals(actual, item.Manifest.SourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new CodeSourceChangedDuringIndexingException(entry.Path);
            }

            result.Add(
                $"{item.Manifest.PluginId.Value}\n{item.Manifest.SourcePath}",
                new MaterializedSource(content));
        }

        return result;
    }

    private static void Report(
        Action<CodeIndexingDiagnostics>? callback,
        CodeIndexingDiagnostics diagnostics)
    {
        try
        {
            callback?.Invoke(diagnostics);
        }
        catch
        {
            // Diagnostics must never change indexing success or failure.
        }
    }

    private sealed record MaterializedSource(byte[] Content);

    private sealed class PluginScopedSink(
        ICodeGraphSink inner,
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CodePluginId pluginId,
        string pluginVersion) : ICodeGraphSink
    {
        public CodeGraphBatchLimits Limits => inner.Limits;

        public ValueTask WriteBatchAsync(
            CodeGraphBatch batch,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (batch.Origin.RepositoryId != repositoryId ||
                batch.Origin.IndexRunId != runId ||
                batch.Origin.PluginId != pluginId ||
                !string.Equals(batch.Origin.PluginVersion, pluginVersion, StringComparison.Ordinal))
            {
                throw new CodeGraphBatchRejectedException(
                    "A plugin emitted a batch outside its assigned lifecycle scope.",
                    [
                        new CodeGraphValidationError(
                            CodeGraphValidationErrorKind.OwnershipMismatch,
                            "lifecycle.batch.ownership-mismatch",
                            "Batch repository, run, plugin, and version must match the executing plugin.")
                    ]);
            }

            return inner.WriteBatchAsync(batch, cancellationToken);
        }
    }
}

public sealed class CodeSourceChangedDuringIndexingException(string path)
    : Exception($"Repository source '{path}' changed between planning and extraction.")
{
    public string Path { get; } = path;
}

public sealed class CodeSourceSizeLimitException(
    string path,
    long maximumBytes,
    bool totalLimit) : Exception(totalLimit
        ? $"Repository source materialization exceeded the total limit of {maximumBytes} bytes."
        : $"Repository source '{path}' exceeded the limit of {maximumBytes} bytes.")
{
    public string Path { get; } = path;
    public long MaximumBytes { get; } = maximumBytes;
    public bool IsTotalLimit { get; } = totalLimit;
}
