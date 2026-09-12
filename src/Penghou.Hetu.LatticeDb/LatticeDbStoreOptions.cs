namespace Penghou.Hetu.LatticeDb;

/// <summary>Optional tuning for <see cref="Hetu.LatticeCodeGraphStore"/>.</summary>
/// <remarks>Every property is optional; unset properties keep engine defaults.</remarks>
public sealed record LatticeDbStoreOptions
{
    /// <summary>Cache budget in MiB.</summary>
    public uint? CacheSizeMb { get; init; }

    /// <summary>Database page size in bytes.</summary>
    public uint? PageSize { get; init; }

    /// <summary>Enables write-ahead logging.</summary>
    public bool? EnableWal { get; init; }

    /// <summary>Enables the in-memory adjacency cache.</summary>
    public bool? EnableAdjacencyCache { get; init; }

    /// <summary>
    /// Serves <c>TraverseAsync</c> without evidence filters from the
    /// experimental native graph mirror instead of the materialized
    /// projection. The mirror is derived state rebuilt on every publication;
    /// provenance-bound queries always use the projection.
    /// </summary>
    public bool UseNativeTraversal { get; init; }
}
