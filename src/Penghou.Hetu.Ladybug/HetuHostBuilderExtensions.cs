namespace Penghou.Hetu.Ladybug;

/// <summary>Ladybug-specific extensions for <see cref="HetuHostBuilder"/>.</summary>
public static class HetuHostBuilderExtensions
{
    /// <summary>Configures the host to use a durable LadybugDB store at the supplied path.</summary>
    public static HetuHostBuilder UseLadybugStore(
        this HetuHostBuilder builder,
        string databasePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return builder.UseStore(() => new LadybugCodeGraphStore(databasePath));
    }
}
