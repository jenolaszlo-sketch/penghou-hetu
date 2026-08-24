namespace Penghou.Hetu;

/// <summary>Identifies the plugin and replaceable index unit that owns a fact batch.</summary>
public sealed record CodeFactOrigin
{
    public CodeFactOrigin(
        CodeRepositoryId repositoryId,
        CodePluginId pluginId,
        string pluginVersion,
        CodeIndexRunId indexRunId,
        CodeIndexUnitId indexUnitId,
        string? sourcePath = null,
        string? sourceHash = null)
    {
        RepositoryId = repositoryId ??
            throw new ArgumentNullException(nameof(repositoryId));
        PluginId = pluginId ??
            throw new ArgumentNullException(nameof(pluginId));
        PluginVersion = ContractValue.Identifier(
            pluginVersion,
            nameof(pluginVersion));
        IndexRunId = indexRunId ??
            throw new ArgumentNullException(nameof(indexRunId));
        IndexUnitId = indexUnitId ??
            throw new ArgumentNullException(nameof(indexUnitId));
        SourcePath = sourcePath is null
            ? null
            : ContractValue.RelativePath(sourcePath, nameof(sourcePath));
        SourceHash = sourceHash is null
            ? null
            : ContractValue.Identifier(sourceHash, nameof(sourceHash));

        if ((SourcePath is null) != (SourceHash is null))
        {
            throw new ArgumentException(
                "Source path and source hash must be supplied together.");
        }
    }

    public CodeRepositoryId RepositoryId { get; }
    public CodePluginId PluginId { get; }
    public string PluginVersion { get; }
    public CodeIndexRunId IndexRunId { get; }
    public CodeIndexUnitId IndexUnitId { get; }
    public string? SourcePath { get; }
    public string? SourceHash { get; }
}

/// <summary>A bounded contribution to one atomically replaceable index unit.</summary>
public sealed record CodeGraphBatch
{
    public CodeGraphBatch(
        CodeFactOrigin origin,
        IReadOnlyList<CodeGraphNode>? nodes = null,
        IReadOnlyList<CodeGraphDeclaration>? declarations = null,
        IReadOnlyList<CodeGraphEdge>? edges = null,
        bool completesIndexUnit = false)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Nodes = Copy(nodes, nameof(nodes));
        Declarations = Copy(declarations, nameof(declarations));
        Edges = Copy(edges, nameof(edges));
        CompletesIndexUnit = completesIndexUnit;
    }

    public CodeFactOrigin Origin { get; }
    public IReadOnlyList<CodeGraphNode> Nodes { get; }
    public IReadOnlyList<CodeGraphDeclaration> Declarations { get; }
    public IReadOnlyList<CodeGraphEdge> Edges { get; }

    /// <summary>
    /// Indicates that this is the final batch for the index unit. A sink must
    /// not expose replacement facts until it receives a completing batch.
    /// </summary>
    public bool CompletesIndexUnit { get; }

    private static IReadOnlyList<T> Copy<T>(
        IReadOnlyList<T>? values,
        string parameterName)
        where T : class
    {
        if (values is null)
            return [];
        if (values.Any(value => value is null))
        {
            throw new ArgumentException(
                "Graph batches cannot contain null facts.",
                parameterName);
        }

        return values.ToArray();
    }
}

/// <summary>Hard limits a plugin must respect for each emitted batch.</summary>
public sealed record CodeGraphBatchLimits
{
    public CodeGraphBatchLimits(
        int maxNodes = 2_000,
        int maxDeclarations = 2_000,
        int maxEdges = 5_000,
        int maxPropertiesPerFact = 64,
        int maxTextPropertyLength = 32_768,
        int maxTextListItems = 1_024,
        int maxBatchesPerIndexUnit = 1_024,
        int maxFactsPerIndexUnit = 1_000_000)
    {
        MaxNodes = Positive(maxNodes, nameof(maxNodes));
        MaxDeclarations = Positive(maxDeclarations, nameof(maxDeclarations));
        MaxEdges = Positive(maxEdges, nameof(maxEdges));
        MaxPropertiesPerFact = Positive(
            maxPropertiesPerFact,
            nameof(maxPropertiesPerFact));
        MaxTextPropertyLength = Positive(
            maxTextPropertyLength,
            nameof(maxTextPropertyLength));
        MaxTextListItems = Positive(
            maxTextListItems,
            nameof(maxTextListItems));
        MaxBatchesPerIndexUnit = Positive(
            maxBatchesPerIndexUnit,
            nameof(maxBatchesPerIndexUnit));
        MaxFactsPerIndexUnit = Positive(
            maxFactsPerIndexUnit,
            nameof(maxFactsPerIndexUnit));
    }

    public int MaxNodes { get; }
    public int MaxDeclarations { get; }
    public int MaxEdges { get; }
    public int MaxPropertiesPerFact { get; }
    public int MaxTextPropertyLength { get; }
    public int MaxTextListItems { get; }
    public int MaxBatchesPerIndexUnit { get; }
    public int MaxFactsPerIndexUnit { get; }

    private static int Positive(int value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>Receives validated, bounded facts from one extraction session.</summary>
public interface ICodeGraphSink
{
    CodeGraphBatchLimits Limits { get; }

    ValueTask WriteBatchAsync(
        CodeGraphBatch batch,
        CancellationToken cancellationToken = default);
}
