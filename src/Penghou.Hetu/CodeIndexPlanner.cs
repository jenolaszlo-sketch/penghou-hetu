using System.Security.Cryptography;

namespace Penghou.Hetu;

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
        CodeSourceManifest manifest,
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
    public CodeSourceManifest Manifest { get; }
    public CodeRepositoryEntry? Source { get; }
}

/// <summary>Immutable incremental work plan ordered by plugin and source path.</summary>
public sealed record CodeIndexPlan
{
    public CodeIndexPlan(
        IReadOnlyList<CodeIndexPlanItem> items,
        int repositoryEntries = 0,
        int unsupportedEntries = 0,
        long hashBytesRead = 0,
        int excludedDirectories = 0,
        int depthLimitedDirectories = 0,
        int reparsePointsSkipped = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(item => item is null))
            throw new ArgumentException("Index plans cannot contain null items.", nameof(items));
        if (repositoryEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(repositoryEntries));
        if (unsupportedEntries < 0 || unsupportedEntries > repositoryEntries)
            throw new ArgumentOutOfRangeException(nameof(unsupportedEntries));
        if (hashBytesRead < 0)
            throw new ArgumentOutOfRangeException(nameof(hashBytesRead));
        if (excludedDirectories < 0 || depthLimitedDirectories < 0 || reparsePointsSkipped < 0)
            throw new ArgumentOutOfRangeException(nameof(excludedDirectories));
        Items = items.ToArray();
        RepositoryEntries = repositoryEntries;
        UnsupportedEntries = unsupportedEntries;
        HashBytesRead = hashBytesRead;
        ExcludedDirectories = excludedDirectories;
        DepthLimitedDirectories = depthLimitedDirectories;
        ReparsePointsSkipped = reparsePointsSkipped;
    }

    public IReadOnlyList<CodeIndexPlanItem> Items { get; }
    public int RepositoryEntries { get; }
    public int UnsupportedEntries { get; }
    public long HashBytesRead { get; }
    public int ExcludedDirectories { get; }
    public int DepthLimitedDirectories { get; }
    public int ReparsePointsSkipped { get; }
}

/// <summary>Controls bounded discovery and explicit plugin selection.</summary>
public sealed record CodeIndexPlanningOptions
{
    public CodeIndexPlanningOptions(
        CodeRepositoryEnumerationOptions? enumeration = null,
        IReadOnlyCollection<CodePluginId>? pluginIds = null,
        long maxHashSourceBytes = 16 * 1024 * 1024,
        long maxHashTotalBytes = 512 * 1024 * 1024)
    {
        if (maxHashSourceBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHashSourceBytes));
        if (maxHashTotalBytes < maxHashSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(maxHashTotalBytes));
        Enumeration = enumeration ?? new CodeRepositoryEnumerationOptions();
        PluginIds = pluginIds?.Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).ToArray() ?? [];
        if (PluginIds.Any(id => id is null))
            throw new ArgumentException("Plugin selections cannot contain null identities.", nameof(pluginIds));
        MaxHashSourceBytes = maxHashSourceBytes;
        MaxHashTotalBytes = maxHashTotalBytes;
    }

    public CodeRepositoryEnumerationOptions Enumeration { get; }
    public IReadOnlyCollection<CodePluginId> PluginIds { get; }
    public long MaxHashSourceBytes { get; }
    public long MaxHashTotalBytes { get; }
}

/// <summary>Builds deterministic, content-addressed incremental indexing plans.</summary>
public sealed class CodeIndexPlanner(CodeGraphPluginRegistry plugins)
{
    private readonly CodeGraphPluginRegistry _plugins = plugins ??
        throw new ArgumentNullException(nameof(plugins));

    public async ValueTask<CodeIndexPlan> CreatePlanAsync(
        ICodeRepositorySource repository,
        IReadOnlyCollection<CodeSourceManifest>? previousManifests = null,
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
        var repositoryEntries = 0;
        var unsupportedEntries = 0;
        long hashBytesRead = 0;
        var excludedDirectories = 0;
        var depthLimitedDirectories = 0;
        var reparsePointsSkipped = 0;
        var enumeration = new CodeRepositoryEnumerationOptions(
            options.Enumeration.ExcludedDirectoryNames,
            options.Enumeration.MaxDepth,
            options.Enumeration.MaxEntries,
            kind =>
            {
                switch (kind)
                {
                    case CodeRepositoryDiscoveryEventKind.DirectoryExcluded:
                        excludedDirectories++;
                        break;
                    case CodeRepositoryDiscoveryEventKind.DepthLimitReached:
                        depthLimitedDirectories++;
                        break;
                    case CodeRepositoryDiscoveryEventKind.ReparsePointSkipped:
                        reparsePointsSkipped++;
                        break;
                }
                options.Enumeration.Report(kind);
            });

        await foreach (var entry in repository.EnumerateAsync(enumeration, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            repositoryEntries++;
            var plugin = _plugins.Resolve(entry.Path);
            if (plugin is null)
            {
                unsupportedEntries++;
                continue;
            }
            if (selectedIds.Count > 0 && !selectedIds.Contains(plugin.Id))
                continue;

            string hash;
            if (entry.ContentHash is not null)
            {
                hash = entry.ContentHash;
            }
            else
            {
                var computed = await ComputeHashAsync(
                    repository,
                    entry,
                    options.MaxHashSourceBytes,
                    options.MaxHashTotalBytes,
                    hashBytesRead,
                    cancellationToken).ConfigureAwait(false);
                hash = computed.Hash;
                hashBytesRead += computed.BytesRead;
            }
            var manifest = new CodeSourceManifest(
                plugin.Id,
                plugin.Version,
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

        return new CodeIndexPlan(
            items
                .OrderBy(item => item.Manifest.PluginId.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Manifest.SourcePath, StringComparer.Ordinal)
                .ToArray(),
            repositoryEntries,
            unsupportedEntries,
            hashBytesRead,
            excludedDirectories,
            depthLimitedDirectories,
            reparsePointsSkipped);
    }

    private static async ValueTask<(string Hash, long BytesRead)> ComputeHashAsync(
        ICodeRepositorySource repository,
        CodeRepositoryEntry entry,
        long maxSourceBytes,
        long maxTotalBytes,
        long priorBytesRead,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maxSourceBytes)
            throw new CodeSourceSizeLimitException(entry.Path, maxSourceBytes, false);
        await using var stream = await repository.OpenReadAsync(entry, cancellationToken)
            .ConfigureAwait(false);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long bytesRead = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            bytesRead += read;
            if (bytesRead > maxSourceBytes)
                throw new CodeSourceSizeLimitException(entry.Path, maxSourceBytes, false);
            if (priorBytesRead + bytesRead > maxTotalBytes)
                throw new CodeSourceSizeLimitException(entry.Path, maxTotalBytes, true);
            hasher.AppendData(buffer, 0, read);
        }

        return ($"sha256:{Convert.ToHexStringLower(hasher.GetHashAndReset())}", bytesRead);
    }

    private static string ManifestKey(CodeSourceManifest manifest) =>
        $"{manifest.PluginId.Value}\n{manifest.SourcePath}";
}
