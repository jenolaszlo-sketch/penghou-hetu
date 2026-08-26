namespace Penghou.Hetu;

/// <summary>Repository-wide inputs available while creating an extraction session.</summary>
public sealed record CodeGraphPluginContext
{
    public CodeGraphPluginContext(
        CodeRepositoryId repositoryId,
        string? repositoryLocation,
        CodeIndexRunId indexRunId,
        IReadOnlyList<CodeGraphSource> sources,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyList<CodeGraphSourceChange>? changes = null)
    {
        RepositoryId = repositoryId ??
            throw new ArgumentNullException(nameof(repositoryId));
        RepositoryLocation = string.IsNullOrWhiteSpace(repositoryLocation)
            ? null
            : repositoryLocation;
        IndexRunId = indexRunId ??
            throw new ArgumentNullException(nameof(indexRunId));
        Sources = sources?.ToArray() ??
            throw new ArgumentNullException(nameof(sources));
        if (Sources.Any(source => source is null))
        {
            throw new ArgumentException(
                "Plugin sources cannot contain null entries.",
                nameof(sources));
        }

        if (Sources
            .GroupBy(source => source.Path, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Plugin sources must have unique repository-relative paths.",
                nameof(sources));
        }

        Changes = changes?.ToArray() ?? [];
        if (Changes.Any(change => change is null))
            throw new ArgumentException("Plugin changes cannot contain null entries.", nameof(changes));
        if (Changes.GroupBy(change => change.Path, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Plugin changes must have unique paths.", nameof(changes));

        Settings = CopySettings(settings);
    }

    public CodeRepositoryId RepositoryId { get; }
    /// <summary>
    /// Gets an optional provider-defined location hint. Plugins must use
    /// <see cref="Sources"/> to read content and must not assume this is a local path.
    /// </summary>
    public string? RepositoryLocation { get; }
    public CodeIndexRunId IndexRunId { get; }
    public IReadOnlyList<CodeGraphSource> Sources { get; }
    public IReadOnlyList<CodeGraphSourceChange> Changes { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }

    private static IReadOnlyDictionary<string, string> CopySettings(
        IReadOnlyDictionary<string, string>? settings)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (settings is null)
            return copy;

        foreach (var (key, value) in settings)
        {
            copy.Add(
                ContractValue.Identifier(key, nameof(settings)),
                value ?? throw new ArgumentException(
                    "Plugin settings cannot contain null values.",
                    nameof(settings)));
        }

        return copy;
    }
}

public enum CodeGraphSourceChangeKind
{
    New = 0,
    Changed = 1,
    Unchanged = 2,
    Deleted = 3
}

/// <summary>Describes one exact source transition visible to an extraction plugin.</summary>
public sealed record CodeGraphSourceChange
{
    public CodeGraphSourceChange(
        string path,
        CodeGraphSourceChangeKind kind,
        string? previousHash,
        string? currentHash)
    {
        Path = ContractValue.RelativePath(path, nameof(path));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == CodeGraphSourceChangeKind.New &&
                (previousHash is not null || currentHash is null) ||
            kind == CodeGraphSourceChangeKind.Deleted &&
                (previousHash is null || currentHash is not null) ||
            kind is CodeGraphSourceChangeKind.Changed or CodeGraphSourceChangeKind.Unchanged &&
                (previousHash is null || currentHash is null))
        {
            throw new ArgumentException("Source hashes do not match the change kind.");
        }

        Kind = kind;
        PreviousHash = previousHash;
        CurrentHash = currentHash;
    }

    public string Path { get; }
    public CodeGraphSourceChangeKind Kind { get; }
    public string? PreviousHash { get; }
    public string? CurrentHash { get; }
}

/// <summary>States allowed on <see cref="CodeRelationshipCoverage"/>.</summary>
public static class CodeRelationshipCoverageState
{
    public const string Produced = "produced";
    public const string Partial = "partial";
    public const string NotProduced = "not-produced";

    public static bool IsDefined(string state) =>
        state is Produced or Partial or NotProduced;
}

/// <summary>
/// Reports whether one relationship kind was actually indexed, so consumers can
/// distinguish "no such relationship exists" from "this extraction did not
/// produce that kind" and from "produced but some targets did not resolve".
/// </summary>
/// <param name="RelationshipKind">The normalized edge kind being reported.</param>
/// <param name="State">Produced, partial, or not-produced.</param>
/// <param name="EdgesEmitted">Edges successfully created for this kind.</param>
/// <param name="UnresolvedTargets">
/// Emission attempts whose target existed but could not be uniquely matched to
/// a graph node. Externally-owned targets (for example base library symbols)
/// are neither emitted nor counted here.
/// </param>
public sealed record CodeRelationshipCoverage
{
    public CodeRelationshipCoverage(
        string relationshipKind,
        string state,
        int edgesEmitted,
        int unresolvedTargets)
    {
        RelationshipKind = ContractValue.Identifier(
            relationshipKind,
            nameof(relationshipKind));
        if (!CodeRelationshipCoverageState.IsDefined(state))
            throw new ArgumentException("Unknown relationship coverage state.", nameof(state));
        if (edgesEmitted < 0 || unresolvedTargets < 0)
            throw new ArgumentOutOfRangeException(nameof(edgesEmitted));
        if (state == CodeRelationshipCoverageState.NotProduced &&
            (edgesEmitted != 0 || unresolvedTargets != 0))
        {
            throw new ArgumentException(
                "A not-produced relationship kind cannot report edges or unresolved targets.",
                nameof(state));
        }

        State = state;
        EdgesEmitted = edgesEmitted;
        UnresolvedTargets = unresolvedTargets;
    }

    public string RelationshipKind { get; }
    public string State { get; }
    public int EdgesEmitted { get; }
    public int UnresolvedTargets { get; }
}

/// <summary>Reports cleanup work and bounded privacy-safe extraction diagnostics.</summary>
public sealed record CodeGraphExtractionResult
{
    public CodeGraphExtractionResult(
        IReadOnlyCollection<CodeIndexUnitId>? obsoleteIndexUnits = null,
        int sourcesExamined = 0,
        int sourcesContributingFacts = 0,
        int unresolvedRelationships = 0,
        IReadOnlyCollection<string>? warningCodes = null,
        IReadOnlyCollection<CodeRelationshipCoverage>? relationshipCoverage = null)
    {
        if (obsoleteIndexUnits?.Any(id => id is null) == true)
            throw new ArgumentException("Obsolete units cannot contain null identities.", nameof(obsoleteIndexUnits));
        ObsoleteIndexUnits = obsoleteIndexUnits?
            .Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray() ?? [];
        if (ObsoleteIndexUnits.Count > 100_000)
            throw new ArgumentException("Extraction results cannot report more than 100,000 obsolete units.", nameof(obsoleteIndexUnits));
        if (sourcesExamined < 0)
            throw new ArgumentOutOfRangeException(nameof(sourcesExamined));
        if (sourcesContributingFacts < 0 || sourcesContributingFacts > sourcesExamined)
            throw new ArgumentOutOfRangeException(nameof(sourcesContributingFacts));
        if (unresolvedRelationships < 0)
            throw new ArgumentOutOfRangeException(nameof(unresolvedRelationships));

        var warnings = warningCodes?.Select(code =>
        {
            var validated = ContractValue.Identifier(code, nameof(warningCodes));
            if (validated.Length > 128)
                throw new ArgumentException("Warning codes cannot exceed 128 characters.", nameof(warningCodes));
            return validated;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (warnings.Length > 100)
            throw new ArgumentException("Extraction results cannot report more than 100 warning codes.", nameof(warningCodes));

        SourcesExamined = sourcesExamined;
        SourcesContributingFacts = sourcesContributingFacts;
        UnresolvedRelationships = unresolvedRelationships;
        WarningCodes = warnings;
        RelationshipCoverage = relationshipCoverage?
            .Select(value => value ?? throw new ArgumentException(
                "Relationship coverage cannot contain null entries.",
                nameof(relationshipCoverage)))
            .GroupBy(value => value.RelationshipKind, StringComparer.Ordinal)
            .Select(group => group.Single())
            .Order(Comparer<CodeRelationshipCoverage>.Create(
                (left, right) => string.CompareOrdinal(
                    left.RelationshipKind,
                    right.RelationshipKind)))
            .ToArray() ?? [];
    }

    public IReadOnlyCollection<CodeIndexUnitId> ObsoleteIndexUnits { get; }
    public int SourcesExamined { get; }
    public int SourcesContributingFacts { get; }
    public int UnresolvedRelationships { get; }
    public IReadOnlyCollection<string> WarningCodes { get; }

    /// <summary>
    /// Per-kind relationship coverage. Every kind the plugin is specified to
    /// produce appears exactly once, including kinds deliberately not produced.
    /// </summary>
    public IReadOnlyCollection<CodeRelationshipCoverage> RelationshipCoverage { get; }
}

/// <summary>Discovers normalized graph facts for one language.</summary>
public interface ICodeGraphPlugin
{
    CodePluginId Id { get; }
    string Version { get; }
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    CodeGraphCapabilities Capabilities { get; }

    bool CanHandle(string path);

    ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
        CodeGraphPluginContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>A repository-aware extraction lifetime owned by one plugin.</summary>
public interface ICodeGraphExtractionSession : IAsyncDisposable
{
    ValueTask<CodeGraphExtractionResult> ExtractAsync(
        ICodeGraphSink sink,
        CancellationToken cancellationToken = default);
}
