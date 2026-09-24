using System.Collections.ObjectModel;

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
        IReadOnlyList<CodeGraphSourceChange>? changes = null,
        IReadOnlyCollection<CodeIndexUnitId>? previousIndexUnits = null)
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
        PreviousIndexUnits = Array.AsReadOnly((previousIndexUnits ?? [])
            .Select(unit => unit ?? throw new ArgumentException(
                "Previous index units cannot contain null identities.",
                nameof(previousIndexUnits)))
            .Distinct()
            .OrderBy(unit => unit.Value, StringComparer.Ordinal)
            .ToArray());
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
    /// <summary>Gets the plugin-owned units in the previous successful publication.</summary>
    public IReadOnlyCollection<CodeIndexUnitId> PreviousIndexUnits { get; }

    private static IReadOnlyDictionary<string, string> CopySettings(
        IReadOnlyDictionary<string, string>? settings)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (settings is null)
            return new ReadOnlyDictionary<string, string>(copy);

        foreach (var (key, value) in settings)
        {
            copy.Add(
                ContractValue.Identifier(key, nameof(settings)),
                value ?? throw new ArgumentException(
                    "Plugin settings cannot contain null values.",
                    nameof(settings)));
        }

        return new ReadOnlyDictionary<string, string>(copy);
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
public enum CodeRelationshipCoverageState
{
    Produced = 0,
    Partial = 1,
    NotProduced = 2,
    /// <summary>
    /// The plugin could not determine coverage (for example an index unit
    /// with no compilable sources). Unlike <see cref="NotProduced"/>, which
    /// records a deliberate omission, this state must never be read as an
    /// empty result.
    /// </summary>
    Unavailable = 3,
    /// <summary>
    /// The relationship kind does not apply to this plugin or index unit.
    /// </summary>
    NotApplicable = 4
}

/// <summary>
/// Reports whether one relationship kind was actually indexed, so consumers can
/// distinguish "no such relationship exists" from "this extraction did not
/// produce that kind" and from "produced but some targets did not resolve".
/// </summary>
/// <param name="RelationshipKind">The normalized edge kind being reported.</param>
/// <param name="State">Produced, partial, not-produced, unavailable, or not-applicable.</param>
/// <param name="EdgesEmitted">Edges successfully created for this kind.</param>
/// <param name="UnresolvedTargets">
/// Emission attempts whose target existed but could not be uniquely matched to
/// a graph node. Externally-owned targets (for example base library symbols)
/// are neither emitted nor counted here.
/// </param>
/// <param name="Candidates">Relationship sites examined for this kind.</param>
/// <param name="InternalTargets">Targets resolved within the same index unit.</param>
/// <param name="CrossProjectTargets">Targets resolved to another in-repo index unit.</param>
/// <param name="ExternalTargets">Externally-owned targets, never emitted or guessed.</param>
/// <param name="AmbiguousTargets">Targets with multiple indexed candidates, never guessed.</param>
/// <param name="UnsupportedTargets">Sites deliberately omitted as unsupported.</param>
public sealed record CodeRelationshipCoverage
{
    public CodeRelationshipCoverage(
        string relationshipKind,
        CodeRelationshipCoverageState state,
        int edgesEmitted,
        int unresolvedTargets,
        int candidates = 0,
        int internalTargets = 0,
        int crossProjectTargets = 0,
        int externalTargets = 0,
        int ambiguousTargets = 0,
        int unsupportedTargets = 0)
    {
        RelationshipKind = ContractValue.Identifier(
            relationshipKind,
            nameof(relationshipKind));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (edgesEmitted < 0 || unresolvedTargets < 0 || candidates < 0 ||
            internalTargets < 0 || crossProjectTargets < 0 || externalTargets < 0 ||
            ambiguousTargets < 0 || unsupportedTargets < 0)
            throw new ArgumentOutOfRangeException(nameof(edgesEmitted));
        if (state is CodeRelationshipCoverageState.NotProduced or
                CodeRelationshipCoverageState.Unavailable or
                CodeRelationshipCoverageState.NotApplicable &&
            (edgesEmitted != 0 || unresolvedTargets != 0 || candidates != 0 ||
             internalTargets != 0 || crossProjectTargets != 0 || externalTargets != 0 ||
             ambiguousTargets != 0 || unsupportedTargets != 0))
        {
            throw new ArgumentException(
                "A not-produced, unavailable, or not-applicable relationship kind cannot report counts.",
                nameof(state));
        }
        if (edgesEmitted != internalTargets + crossProjectTargets)
        {
            throw new ArgumentException(
                "Emitted edges must equal internal plus cross-project targets.",
                nameof(edgesEmitted));
        }

        State = state;
        EdgesEmitted = edgesEmitted;
        UnresolvedTargets = unresolvedTargets;
        Candidates = candidates;
        InternalTargets = internalTargets;
        CrossProjectTargets = crossProjectTargets;
        ExternalTargets = externalTargets;
        AmbiguousTargets = ambiguousTargets;
        UnsupportedTargets = unsupportedTargets;
    }

    public string RelationshipKind { get; }
    public CodeRelationshipCoverageState State { get; }
    public int EdgesEmitted { get; }
    public int UnresolvedTargets { get; }
    public int Candidates { get; }
    public int InternalTargets { get; }
    public int CrossProjectTargets { get; }
    public int ExternalTargets { get; }
    public int AmbiguousTargets { get; }
    public int UnsupportedTargets { get; }
}

/// <summary>
/// Per-index-unit relationship coverage. Run-wide
/// <see cref="CodeGraphExtractionResult.RelationshipCoverage"/> answers "what
/// did this extraction produce"; these entries answer "why is this category
/// empty or partial for one project unit".
/// </summary>
/// <param name="IndexUnitId">The plugin-owned unit this entry describes.</param>
/// <param name="Coverage">One entry per relationship kind, as run-wide.</param>
public sealed record CodeIndexUnitCoverage
{
    public CodeIndexUnitCoverage(
        CodeIndexUnitId indexUnitId,
        IReadOnlyCollection<CodeRelationshipCoverage>? coverage = null)
    {
        IndexUnitId = indexUnitId ??
            throw new ArgumentNullException(nameof(indexUnitId));
        Coverage = coverage?
            .Select(value => value ?? throw new ArgumentException(
                "Index-unit coverage cannot contain null entries.",
                nameof(coverage)))
            .GroupBy(value => value.RelationshipKind, StringComparer.Ordinal)
            .Select(group => group.Single())
            .Order(Comparer<CodeRelationshipCoverage>.Create(
                (left, right) => string.CompareOrdinal(
                    left.RelationshipKind,
                    right.RelationshipKind)))
            .ToArray() ?? [];
    }

    public CodeIndexUnitId IndexUnitId { get; }
    public IReadOnlyCollection<CodeRelationshipCoverage> Coverage { get; }
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
        IReadOnlyCollection<CodeRelationshipCoverage>? relationshipCoverage = null,
        IReadOnlyCollection<CodeIndexUnitCoverage>? indexUnitCoverage = null)
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
        IndexUnitCoverage = indexUnitCoverage?
            .Select(value => value ?? throw new ArgumentException(
                "Index-unit coverage cannot contain null entries.",
                nameof(indexUnitCoverage)))
            .GroupBy(value => value.IndexUnitId.Value, StringComparer.Ordinal)
            .Select(group => group.Single())
            .OrderBy(value => value.IndexUnitId.Value, StringComparer.Ordinal)
            .ToArray() ?? [];
        if (IndexUnitCoverage.Count > 100_000)
            throw new ArgumentException(
                "Extraction results cannot report more than 100,000 index-unit coverage entries.",
                nameof(indexUnitCoverage));
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

    /// <summary>
    /// Per-index-unit coverage breakdown, ordered by unit id. Empty when the
    /// plugin reports run-wide coverage only.
    /// </summary>
    public IReadOnlyCollection<CodeIndexUnitCoverage> IndexUnitCoverage { get; }
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
