using System.Runtime.CompilerServices;

namespace Penghou.Hetu;

/// <summary>Identifies a logical repository and the provider-specific location to open.</summary>
public sealed record CodeRepositoryDescriptor
{
    public CodeRepositoryDescriptor(
        CodeRepositoryId id,
        string location,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        Location = location;
        var settingsCopy = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                if (value is null)
                {
                    throw new ArgumentException(
                        "Repository settings cannot contain null values.",
                        nameof(settings));
                }

                settingsCopy.Add(key, value);
            }
        }

        Settings = settingsCopy;
    }

    public CodeRepositoryId Id { get; }
    public string Location { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>Metadata for one repository-relative source entry.</summary>
public sealed record CodeRepositoryEntry
{
    public CodeRepositoryEntry(
        string path,
        long? length = null,
        DateTimeOffset? lastModifiedAt = null,
        string? contentHash = null,
        string? version = null)
    {
        Path = NormalizeRelativePath(path);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        LastModifiedAt = lastModifiedAt;
        ContentHash = string.IsNullOrWhiteSpace(contentHash)
            ? null
            : contentHash;
        Version = string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public string Path { get; }
    public long? Length { get; }
    public DateTimeOffset? LastModifiedAt { get; }
    public string? ContentHash { get; }
    public string? Version { get; }

    internal static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (System.IO.Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Repository entries require normalized relative paths.",
                nameof(path));
        }

        return normalized;
    }
}

/// <summary>Bounds and filters one repository enumeration.</summary>
public sealed record CodeRepositoryEnumerationOptions
{
    private static readonly string[] DefaultExclusions =
    [
        ".git",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "packages",
        "vendor"
    ];

    public CodeRepositoryEnumerationOptions(
        IReadOnlyCollection<string>? excludedDirectoryNames = null,
        int maxDepth = 64,
        int maxEntries = 1_000_000)
    {
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        ExcludedDirectoryNames = (excludedDirectoryNames ?? DefaultExclusions)
            .Select(name =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (name.Contains('/') || name.Contains('\\'))
                {
                    throw new ArgumentException(
                        "Directory exclusions must be names, not paths.",
                        nameof(excludedDirectoryNames));
                }

                return name;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MaxDepth = maxDepth;
        MaxEntries = maxEntries;
    }

    public IReadOnlyCollection<string> ExcludedDirectoryNames { get; }
    public int MaxDepth { get; }
    public int MaxEntries { get; }
}

/// <summary>Lazy provider-neutral access to one repository snapshot or live view.</summary>
public interface ICodeRepositorySource : IAsyncDisposable
{
    CodeRepositoryId RepositoryId { get; }
    string ProviderName { get; }

    /// <summary>Gets a provider-defined immutable snapshot identity when available.</summary>
    string? SnapshotIdentity { get; }

    /// <summary>Gets whether enumeration and reads are guaranteed to use one snapshot.</summary>
    bool IsConsistentSnapshot { get; }

    IAsyncEnumerable<CodeRepositoryEntry> EnumerateAsync(
        CodeRepositoryEnumerationOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenReadAsync(
        CodeRepositoryEntry entry,
        CancellationToken cancellationToken = default);
}

/// <summary>Opens repository sources for one storage or workspace technology.</summary>
public interface ICodeRepositoryProvider
{
    string Name { get; }

    bool CanOpen(CodeRepositoryDescriptor repository);

    ValueTask<ICodeRepositorySource> OpenAsync(
        CodeRepositoryDescriptor repository,
        CancellationToken cancellationToken = default);
}

/// <summary>Default provider for repositories stored in a local directory.</summary>
public sealed class FileSystemCodeRepositoryProvider : ICodeRepositoryProvider
{
    public string Name => "filesystem";

    public bool CanOpen(CodeRepositoryDescriptor repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return TryResolveRoot(repository.Location, out _);
    }

    public ValueTask<ICodeRepositorySource> OpenAsync(
        CodeRepositoryDescriptor repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveRoot(repository.Location, out var root))
        {
            throw new ArgumentException(
                "The repository location is not a local absolute path or file URI.",
                nameof(repository));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Repository directory '{root}' does not exist.");
        }

        return new(new FileSystemCodeRepositorySource(repository.Id, root));
    }

    private static bool TryResolveRoot(string location, out string root)
    {
        root = string.Empty;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            root = Path.GetFullPath(uri.LocalPath);
            return true;
        }

        if (!Path.IsPathRooted(location))
            return false;
        root = Path.GetFullPath(location);
        return true;
    }
}

internal sealed class FileSystemCodeRepositorySource(
    CodeRepositoryId repositoryId,
    string root) : ICodeRepositorySource
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(root));
    private bool _disposed;

    public CodeRepositoryId RepositoryId { get; } = repositoryId;
    public string ProviderName => "filesystem";
    public string? SnapshotIdentity => null;
    public bool IsConsistentSnapshot => false;

    public async IAsyncEnumerable<CodeRepositoryEntry> EnumerateAsync(
        CodeRepositoryEnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var exclusions = options.ExcludedDirectoryNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((_root, 0));
        var count = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var childDirectories = new List<string>();

            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isDirectory)
                {
                    if (depth < options.MaxDepth &&
                        !exclusions.Contains(Path.GetFileName(path)) &&
                        !isReparsePoint)
                    {
                        childDirectories.Add(path);
                    }

                    continue;
                }

                if (isReparsePoint)
                    continue;
                count++;
                if (count > options.MaxEntries)
                {
                    throw new CodeRepositoryEnumerationLimitException(
                        options.MaxEntries);
                }

                var info = new FileInfo(path);
                yield return new CodeRepositoryEntry(
                    Path.GetRelativePath(_root, path).Replace('\\', '/'),
                    info.Length,
                    info.LastWriteTimeUtc);
                await Task.Yield();
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
                pending.Push((childDirectories[index], depth + 1));
        }
    }

    public ValueTask<Stream> OpenReadAsync(
        CodeRepositoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(
            entry.Path.Replace('/', Path.DirectorySeparatorChar),
            _root);
        var relative = Path.GetRelativePath(_root, fullPath);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Repository entry resolves outside the repository root.");
        }

        EnsureNoReparsePoints(fullPath);

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(stream);
    }

    private void EnsureNoReparsePoints(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    "Filesystem repository sources do not follow reparse points.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Raised when repository enumeration exceeds its configured entry bound.</summary>
public sealed class CodeRepositoryEnumerationLimitException(int maximumEntries)
    : Exception($"Repository enumeration exceeded the limit of {maximumEntries} entries.")
{
    public int MaximumEntries { get; } = maximumEntries;
}
