namespace Penghou.Hetu;

/// <summary>Stages and atomically publishes normalized graph facts.</summary>
public interface ICodeGraphIndexStore
{
    ValueTask UpsertRepositoryAsync(
        CodeRepositoryManifest repository,
        CancellationToken cancellationToken = default);

    ValueTask<CodeRepositoryManifest?> GetRepositoryAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a running, failed, or cancelled run. Successful completion must use
    /// <see cref="CompleteIndexRunAsync"/> so source state is published atomically.
    /// </summary>
    ValueTask StoreIndexRunAsync(
        CodeIndexRunManifest run,
        CancellationToken cancellationToken = default);

    ValueTask<CodeIndexRunManifest?> GetIndexRunAsync(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically publishes all changes staged for the run, completes it, and
    /// publishes its incremental source state. Failed or cancelled runs must
    /// not replace the last successful graph or source state.
    /// </summary>
    ValueTask CompleteIndexRunAsync(
        CodeIndexRunManifest completedRun,
        CodeRepositoryIndexState state,
        CancellationToken cancellationToken = default);

    ValueTask<CodeRepositoryIndexState?> GetLatestIndexStateAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages an index-unit replacement for its running index run.</summary>
    ValueTask StageIndexUnitAsync(
        CodeIndexUnitReplacement replacement,
        CancellationToken cancellationToken = default);

    /// <summary>Stages an index-unit deletion for a running index run.</summary>
    ValueTask StageIndexUnitDeletionAsync(
        CodeRepositoryId repositoryId,
        CodeIndexRunId indexRunId,
        CodePluginId pluginId,
        CodeIndexUnitId indexUnitId,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the latest successfully published repository graph.</summary>
public interface ICodeGraphReader
{
    /// <summary>Gets the identity of the latest successful publication.</summary>
    ValueTask<CodeGraphPublication?> GetLatestPublicationAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default);

    ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphNode>>?>
        FindNodesByQualifiedNameWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            string qualifiedName,
            CancellationToken cancellationToken = default);

    ValueTask<CodeGraphQueryEnvelope<CodeGraphTraversalResult>?>
        TraverseWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeGraphTraversalQuery query,
            CancellationToken cancellationToken = default);

    ValueTask<CodeGraphQueryEnvelope<IReadOnlyList<CodeGraphDeclaration>>?>
        GetDeclarationsWithProvenanceAsync(
            CodeRepositoryId repositoryId,
            CodeSymbolId symbolId,
            CancellationToken cancellationToken = default);

    ValueTask<CodeGraphNode?> GetNodeAsync(
        CodeRepositoryId repositoryId,
        CodeNodeId nodeId,
        CancellationToken cancellationToken = default);

    ValueTask<CodeGraphNode?> FindSymbolAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CodeGraphNode>> FindNodesByQualifiedNameAsync(
        CodeRepositoryId repositoryId,
        string qualifiedName,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CodeGraphDeclaration>> GetDeclarationsAsync(
        CodeRepositoryId repositoryId,
        CodeSymbolId symbolId,
        CancellationToken cancellationToken = default);

    ValueTask<CodeGraphTraversalResult> TraverseAsync(
        CodeRepositoryId repositoryId,
        CodeGraphTraversalQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined provider contract for hosts that both index and query repositories.
/// Query-only consumers should depend on <see cref="ICodeGraphReader"/>.
/// </summary>
public interface ICodeGraphStore : ICodeGraphIndexStore, ICodeGraphReader;
