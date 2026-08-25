using System.Security.Cryptography;

namespace Penghou.Hetu;

/// <summary>Describes the persisted input identity of one plugin-owned index unit.</summary>
public sealed record CodeIndexUnitManifest
{
    public CodeIndexUnitManifest(
        CodePluginId pluginId,
        string pluginVersion,
        CodeIndexUnitId indexUnitId,
        string sourcePath,
        string sourceHash)
    {
        PluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        PluginVersion = ValidateIdentifier(pluginVersion, nameof(pluginVersion));
        IndexUnitId = indexUnitId ?? throw new ArgumentNullException(nameof(indexUnitId));
        SourcePath = CodeRepositoryEntry.NormalizeRelativePath(sourcePath);
        SourceHash = ValidateIdentifier(sourceHash, nameof(sourceHash));
    }

    public CodePluginId PluginId { get; }
    public string PluginVersion { get; }
    public CodeIndexUnitId IndexUnitId { get; }
    public string SourcePath { get; }
    public string SourceHash { get; }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Values cannot have surrounding whitespace or contain control characters.",
                parameterName);
        }

        return value;
    }
}

public enum CodeIndexPlanStatus
{
    New = 0,
    Changed = 1,
    Unchanged = 2,
    Deleted = 3
}

/// <summary>One deterministic action in an incremental indexing plan.</summary>
public sealed record CodeIndexPlanItem
{
    public CodeIndexPlanItem(
        CodeIndexPlanStatus status,
        CodeIndexUnitManifest manifest,
        CodeRepositoryEntry? source)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == CodeIndexPlanStatus.Deleted) != (source is null))
        {
            throw new ArgumentException(
                "Only deleted plan items omit their current repository source.",
                nameof(source));
        }

        Status = status;
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Source = source;
    }

    public CodeIndexPlanStatus Status { get; }
    public CodeIndexUnitManifest Manifest { get; }
    public CodeRepositoryEntry? Source { get; }
}

/// <summary>Immutable incremental work plan ordered by plugin and source path.</summary>
public sealed record CodeIndexPlan
{
    public CodeIndexPlan(IReadOnlyList<CodeIndexPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(item => item is null))
            throw new ArgumentException("Index plans cannot contain null items.", nameof(items));
        Items = items.ToArray();
    }

    public IReadOnlyList<CodeIndexPlanItem> Items { get; }
}

/// <summary>Controls bounded discovery and explicit plugin selection.</summary>
public sealed record CodeIndexPlanningOptions
{
    public CodeIndexPlanningOptions(
        CodeRepositoryEnumerationOptions? enumeration = null,
        IReadOnlyCollection<CodePluginId>? pluginIds = null)
    {
        Enumeration = enumeration ?? new CodeRepositoryEnumerationOptions();
        PluginIds = pluginIds?.Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).ToArray() ?? [];
        if (PluginIds.Any(id => id is null))
            throw new ArgumentException("Plugin selections cannot contain null identities.", nameof(pluginIds));
    }

    public CodeRepositoryEnumerationOptions Enumeration { get; }
    public IReadOnlyCollection<CodePluginId> PluginIds { get; }
}

/// <summary>Builds deterministic, content-addressed incremental indexing plans.</summary>
public sealed class CodeIndexPlanner(CodeGraphPluginRegistry plugins)
{
    private readonly CodeGraphPluginRegistry _plugins = plugins ??
        throw new ArgumentNullException(nameof(plugins));

    public async ValueTask<CodeIndexPlan> CreatePlanAsync(
        ICodeRepositorySource repository,
        IReadOnlyCollection<CodeIndexUnitManifest>? previousManifests = null,
        CodeIndexPlanningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        options ??= new CodeIndexPlanningOptions();
        var selectedIds = options.PluginIds.ToHashSet();
        var registeredIds = _plugins.Plugins.Select(plugin => plugin.Id).ToHashSet();
        var unknownIds = selectedIds.Except(registeredIds).OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        if (unknownIds.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown plugin identities: {string.Join(", ", unknownIds.Select(id => id.Value))}.",
                nameof(options));
        }

        var previous = (previousManifests ?? [])
            .ToDictionary(ManifestKey, StringComparer.Ordinal);
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CodeIndexPlanItem>();

        await foreach (var entry in repository.EnumerateAsync(options.Enumeration, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = _plugins.Resolve(entry.Path);
            if (plugin is null || (selectedIds.Count > 0 && !selectedIds.Contains(plugin.Id)))
                continue;

            var hash = entry.ContentHash ??
                await ComputeHashAsync(repository, entry, cancellationToken).ConfigureAwait(false);
            var manifest = new CodeIndexUnitManifest(
                plugin.Id,
                plugin.Version,
                CreateIndexUnitId(plugin.Id, entry.Path),
                entry.Path,
                hash);
            var key = ManifestKey(manifest);
            if (!currentKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Repository provider '{repository.ProviderName}' returned duplicate entry '{entry.Path}'.");
            }
            var status = !previous.TryGetValue(key, out var old)
                ? CodeIndexPlanStatus.New
                : old.PluginVersion == manifest.PluginVersion && old.SourceHash == manifest.SourceHash
                    ? CodeIndexPlanStatus.Unchanged
                    : CodeIndexPlanStatus.Changed;
            items.Add(new(status, manifest, entry));
        }

        foreach (var old in previous.Values)
        {
            if (!currentKeys.Contains(ManifestKey(old)) &&
                (selectedIds.Count == 0 || selectedIds.Contains(old.PluginId)))
            {
                items.Add(new(CodeIndexPlanStatus.Deleted, old, null));
            }
        }

        return new CodeIndexPlan(items
            .OrderBy(item => item.Manifest.PluginId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Manifest.SourcePath, StringComparer.Ordinal)
            .ToArray());
    }

    private static async ValueTask<string> ComputeHashAsync(
        ICodeRepositorySource repository,
        CodeRepositoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = await repository.OpenReadAsync(entry, cancellationToken)
            .ConfigureAwait(false);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    private static CodeIndexUnitId CreateIndexUnitId(CodePluginId pluginId, string path)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return new CodeIndexUnitId($"{pluginId.Value}:source:{Convert.ToHexStringLower(bytes)}");
    }

    private static string ManifestKey(CodeIndexUnitManifest manifest) =>
        $"{manifest.PluginId.Value}\n{manifest.SourcePath}";
}
