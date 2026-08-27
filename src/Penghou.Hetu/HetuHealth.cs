namespace Penghou.Hetu;

public enum CodeGraphStoreHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Unhealthy = 2
}

/// <summary>Provider-neutral, content-free graph-store readiness information.</summary>
public sealed record CodeGraphStoreHealth(
    CodeGraphStoreHealthStatus Status,
    string StoreName,
    string? Detail = null,
    int? SchemaVersion = null,
    int? RepositoryCount = null,
    int? RunCount = null,
    int? IndexUnitCount = null)
{
    public bool IsHealthy => Status == CodeGraphStoreHealthStatus.Healthy;
}

/// <summary>Optional readiness contract implemented by graph stores.</summary>
public interface ICodeGraphStoreHealthCheck
{
    ValueTask<CodeGraphStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Readiness plus the deterministic host composition being checked.</summary>
public sealed record HetuHostHealth(
    CodeGraphStoreHealth Store,
    IReadOnlyList<CodePluginId> PluginIds,
    IReadOnlyList<string> RepositoryProviderNames)
{
    public bool IsReady => Store.IsHealthy;
}
