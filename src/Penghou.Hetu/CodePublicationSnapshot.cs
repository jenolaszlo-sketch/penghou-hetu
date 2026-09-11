using System.Security.Cryptography;
using System.Text.Json;

namespace Penghou.Hetu;

/// <summary>A publication snapshot export or import was rejected.</summary>
public sealed class CodePublicationSnapshotException : Exception
{
    public CodePublicationSnapshotException(string message)
        : base(message) { }
}

/// <summary>Bounds for publication snapshot export and import.</summary>
public sealed record CodeSnapshotOptions
{
    public int MaxUnits { get; init; } = 4096;
    public int MaxNodes { get; init; } = 100_000;
    public int MaxEdges { get; init; } = 200_000;
    public int MaxBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>
/// An integrity-checked capture of one successful publication: the repository,
/// completed run, source state, and every published index unit. Importing the
/// snapshot through the normal staging path reproduces the exact graph, so a
/// later query can reproduce the original result after newer publications move
/// on. There is no format migration: schema mismatches are rejected.
/// </summary>
public sealed record CodePublicationSnapshot
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions HashOptions = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required CodeRepositoryManifest Repository { get; init; }
    public required CodeIndexRunManifest CompletedRun { get; init; }
    public required CodeRepositoryIndexState IndexState { get; init; }
    public required IReadOnlyList<CodeIndexUnitReplacement> Units { get; init; }
    public required string IntegrityHash { get; init; }

    /// <summary>
    /// Captures the latest successful publication of <paramref name="repositoryId"/>.
    /// The publication is re-read after the state so a concurrent publication
    /// moving underneath the export fails explicitly instead of mixing runs.
    /// </summary>
    public static async ValueTask<CodePublicationSnapshot> ExportAsync(
        ICodeGraphStore source,
        CodeRepositoryId repositoryId,
        CodeSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(repositoryId);
        var bounds = options ?? new CodeSnapshotOptions();
        ValidateBounds(bounds);
        var publication = await source.GetLatestPublicationAsync(repositoryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CodePublicationSnapshotException(
                "The repository has no successful publication to export.");
        var repository = await source.GetRepositoryAsync(repositoryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CodePublicationSnapshotException(
                "The repository manifest is missing for the exported publication.");
        var run = await source.GetIndexRunAsync(repositoryId, publication.IndexRunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null || run.Status != CodeIndexRunStatus.Completed)
            throw new CodePublicationSnapshotException(
                "The exported publication has no completed index run.");
        var state = await source.GetLatestIndexStateAsync(repositoryId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null || state.IndexRunId != publication.IndexRunId)
            throw new CodePublicationSnapshotException(
                "The publication moved during export; retry the export.");
        var units = (await source.GetPublishedUnitsAsync(repositoryId, cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(unit => unit.Origin.PluginId.Value, StringComparer.Ordinal)
            .ThenBy(unit => unit.Origin.IndexUnitId.Value, StringComparer.Ordinal)
            .ToArray();
        var snapshot = new CodePublicationSnapshot
        {
            Repository = repository,
            CompletedRun = run,
            IndexState = state,
            Units = units,
            IntegrityHash = string.Empty
        };
        var payload = snapshot.PayloadBytes(bounds);
        return snapshot with { IntegrityHash = Hash(payload) };
    }

    /// <summary>
    /// Replays the snapshot into <paramref name="target"/> through the normal
    /// staging path, reproducing the exact publication. Integrity, schema, and
    /// bounds are verified before anything is written.
    /// </summary>
    public async ValueTask ImportAsync(
        ICodeGraphIndexStore target,
        CodeSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var bounds = options ?? new CodeSnapshotOptions();
        ValidateBounds(bounds);
        if (SchemaVersion != CurrentSchemaVersion)
            throw new CodePublicationSnapshotException(
                $"Snapshot schema {SchemaVersion} is not supported; expected {CurrentSchemaVersion}.");
        if (CompletedRun.Status != CodeIndexRunStatus.Completed ||
            IndexState.IndexRunId != CompletedRun.Id ||
            IndexState.RepositoryId != CompletedRun.RepositoryId ||
            Repository.Id != CompletedRun.RepositoryId)
            throw new CodePublicationSnapshotException(
                "The snapshot members do not describe one consistent publication.");
        var payload = PayloadBytes(bounds);
        if (!string.Equals(Hash(payload), IntegrityHash, StringComparison.OrdinalIgnoreCase))
            throw new CodePublicationSnapshotException(
                "The snapshot integrity hash does not match its content.");
        await target.UpsertRepositoryAsync(Repository, cancellationToken).ConfigureAwait(false);
        await target.StoreIndexRunAsync(
            new CodeIndexRunManifest(
                CompletedRun.RepositoryId,
                CompletedRun.Id,
                CompletedRun.StartedAt,
                CodeIndexRunStatus.Running,
                plugins: CompletedRun.Plugins),
            cancellationToken).ConfigureAwait(false);
        foreach (var unit in Units)
            await target.StageIndexUnitAsync(unit, cancellationToken).ConfigureAwait(false);
        await target.CompleteIndexRunAsync(CompletedRun, IndexState, cancellationToken)
            .ConfigureAwait(false);
    }

    private byte[] PayloadBytes(CodeSnapshotOptions bounds)
    {
        if (Units.Count > bounds.MaxUnits)
            throw new CodePublicationSnapshotException(
                $"The snapshot holds {Units.Count} units, above the limit of {bounds.MaxUnits}.");
        var nodes = 0;
        var edges = 0;
        foreach (var unit in Units)
        {
            nodes += unit.Nodes.Count;
            edges += unit.Edges.Count;
        }
        if (nodes > bounds.MaxNodes)
            throw new CodePublicationSnapshotException(
                $"The snapshot holds {nodes} nodes, above the limit of {bounds.MaxNodes}.");
        if (edges > bounds.MaxEdges)
            throw new CodePublicationSnapshotException(
                $"The snapshot holds {edges} edges, above the limit of {bounds.MaxEdges}.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SnapshotPayload(SchemaVersion, Repository, CompletedRun, IndexState, Units),
            HashOptions);
        if (payload.Length > bounds.MaxBytes)
            throw new CodePublicationSnapshotException(
                $"The snapshot payload is {payload.Length} bytes, above the limit of {bounds.MaxBytes}.");
        return payload;
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static void ValidateBounds(CodeSnapshotOptions bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.MaxUnits < 1 || bounds.MaxNodes < 1 || bounds.MaxEdges < 1 || bounds.MaxBytes < 1)
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "Snapshot bounds must be positive.");
    }

    private sealed record SnapshotPayload(
        int SchemaVersion,
        CodeRepositoryManifest Repository,
        CodeIndexRunManifest Run,
        CodeRepositoryIndexState State,
        IReadOnlyList<CodeIndexUnitReplacement> Units);
}
