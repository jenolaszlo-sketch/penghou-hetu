using System.Collections.Concurrent;
using System.Diagnostics;

namespace Penghou.Hetu;

/// <summary>Bounds and configures one repository indexing lifecycle.</summary>
public sealed record CodeIndexingOptions
{
    public CodeIndexingOptions(
        CodeIndexPlanningOptions? planning = null,
        CodeGraphBatchLimits? batchLimits = null,
        int maxConcurrentPlugins = 1)
    {
        if (maxConcurrentPlugins < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentPlugins));
        Planning = planning ?? new CodeIndexPlanningOptions();
        BatchLimits = batchLimits ?? new CodeGraphBatchLimits();
        MaxConcurrentPlugins = maxConcurrentPlugins;
    }

    public CodeIndexPlanningOptions Planning { get; }
    public CodeGraphBatchLimits BatchLimits { get; }
    public int MaxConcurrentPlugins { get; }
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
        plan = await planner.CreatePlanAsync(
            repository,
            previousState?.Sources,
            options.Planning,
            cancellationToken).ConfigureAwait(false);
        var planningDuration = Stopwatch.GetElapsedTime(planningStarted);
        var selectedPlugins = SelectPlugins(plan, options.Planning);
        var running = new CodeIndexRunManifest(
            descriptor.Id,
            runId,
            startedAt,
            plugins: selectedPlugins.Select(plugin => plugin.Id).ToArray());
        await _store.StoreIndexRunAsync(running, cancellationToken).ConfigureAwait(false);

        try
        {
            var extractionStarted = Stopwatch.GetTimestamp();
            await using var sink = new CodeGraphIngestionSink(
                _store,
                options.BatchLimits,
                onCompleted: ingestion.Add);
            using var concurrency = new SemaphoreSlim(options.MaxConcurrentPlugins);
            var results = new ConcurrentBag<(ICodeGraphPlugin Plugin, CodeGraphExtractionResult Result)>();
            var tasks = selectedPlugins.Select(async plugin =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var context = CreateContext(descriptor, runId, plugin, plan, repository, previousState);
                    await using var session = await plugin.CreateSessionAsync(context, cancellationToken)
                        .ConfigureAwait(false);
                    var result = await session.ExtractAsync(sink, cancellationToken).ConfigureAwait(false);
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
                selectedPlugins.Select(plugin => plugin.Id).ToArray());
            var state = CreateState(descriptor.Id, runId, repository, previousState, plan, selectedPlugins);
            await _store.CompleteIndexRunAsync(completed, state, cancellationToken).ConfigureAwait(false);
            persistenceDuration = Stopwatch.GetElapsedTime(persistenceStarted);
            var final = CreateDiagnostics(
                descriptor.Id, runId, CodeIndexRunStatus.Completed, plan, ingestion,
                pluginsExecuted, unitsDeleted, planningDuration, extractionDuration, persistenceDuration);
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
                pluginsExecuted, unitsDeleted, planningDuration, extractionDuration, persistenceDuration);
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
        ICodeRepositorySource repository,
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
                var entry = item.Source!;
                return new CodeGraphSource(
                    entry.Path,
                    item.Manifest.SourceHash,
                    cancellationToken => repository.OpenReadAsync(entry, cancellationToken));
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
        TimeSpan planningDuration,
        TimeSpan extractionDuration,
        TimeSpan persistenceDuration)
    {
        var units = ingestion.ToArray();
        return new(
            repositoryId,
            runId,
            status,
            plan.Items.Count(item => item.Status != CodeIndexPlanStatus.Deleted),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.New),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Changed),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Unchanged),
            plan.Items.Count(item => item.Status == CodeIndexPlanStatus.Deleted),
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
}
