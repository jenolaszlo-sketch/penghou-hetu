namespace Penghou.Hetu;

/// <summary>Persists normalized graph facts using provider-neutral semantics.</summary>
public interface ICodeGraphStore
{
    ValueTask UpsertRepositoryAsync(
        CodeRepositoryManifest repository,
        CancellationToken cancellationToken = default);

    ValueTask<CodeRepositoryManifest?> GetRepositoryAsync(
        CodeRepositoryId repositoryId,
        CancellationToken cancellationToken = default);

    ValueTask StoreIndexRunAsync(
        CodeIndexRunManifest run,
        CancellationToken cancellationToken = default);

    ValueTask<CodeIndexRunManifest?> GetIndexRunAsync(
        CodeRepositoryId repositoryId,
        CodeIndexRunId runId,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceIndexUnitAsync(
        CodeIndexUnitReplacement replacement,
        CancellationToken cancellationToken = default);

    ValueTask DeleteIndexUnitAsync(
        CodeRepositoryId repositoryId,
        CodePluginId pluginId,
        CodeIndexUnitId indexUnitId,
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
