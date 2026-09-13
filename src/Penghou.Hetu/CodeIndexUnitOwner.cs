namespace Penghou.Hetu;

/// <summary>
/// Identifies one plugin-owned index unit by value. Shared by the
/// in-memory store and the ingestion sink so ownership keys cannot drift.
/// </summary>
internal readonly record struct OwnerKey(
    string RepositoryId,
    string PluginId,
    string IndexUnitId)
{
    public static OwnerKey From(CodeFactOrigin origin) =>
        new(
            origin.RepositoryId.Value,
            origin.PluginId.Value,
            origin.IndexUnitId.Value);
}
