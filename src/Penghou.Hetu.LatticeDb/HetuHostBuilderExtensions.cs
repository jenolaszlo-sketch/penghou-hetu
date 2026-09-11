namespace Penghou.Hetu.LatticeDb;

/// <summary>LatticeDB-specific extensions for <see cref="HetuHostBuilder"/>.</summary>
public static class HetuHostBuilderExtensions
{
    /// <summary>Configures the host to use a durable LatticeDB store at the supplied file path.</summary>
    public static HetuHostBuilder UseLatticeStore(
        this HetuHostBuilder builder,
        string databasePath,
        LatticeDbStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return builder.UseStore(() => new LatticeCodeGraphStore(databasePath, options));
    }
}
