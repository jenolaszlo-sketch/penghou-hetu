namespace Penghou.Hetu;

/// <summary>Describes one repository known to a graph store.</summary>
public sealed record CodeRepositoryManifest
{
    public CodeRepositoryManifest(
        CodeRepositoryId id,
        string? displayName = null,
        string? sourceUri = null,
        DateTimeOffset? registeredAt = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName;
        SourceUri = string.IsNullOrWhiteSpace(sourceUri)
            ? null
            : sourceUri;
        RegisteredAt = registeredAt ?? DateTimeOffset.UtcNow;
    }

    public CodeRepositoryId Id { get; }
    public string? DisplayName { get; }
    public string? SourceUri { get; }
    public DateTimeOffset RegisteredAt { get; }
}

public enum CodeIndexRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>Records one indexing attempt without retaining source content.</summary>
public sealed record CodeIndexRunManifest
{
    public CodeIndexRunManifest(
        CodeRepositoryId repositoryId,
        CodeIndexRunId id,
        DateTimeOffset startedAt,
        CodeIndexRunStatus status = CodeIndexRunStatus.Running,
        DateTimeOffset? completedAt = null,
        IReadOnlyCollection<CodePluginId>? plugins = null)
    {
        RepositoryId = repositoryId ??
            throw new ArgumentNullException(nameof(repositoryId));
        Id = id ?? throw new ArgumentNullException(nameof(id));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (completedAt < startedAt)
            throw new ArgumentOutOfRangeException(nameof(completedAt));
        if (status == CodeIndexRunStatus.Running && completedAt is not null)
        {
            throw new ArgumentException(
                "A running index manifest cannot have a completion time.",
                nameof(completedAt));
        }

        StartedAt = startedAt;
        Status = status;
        CompletedAt = completedAt;
        Plugins = plugins?.Distinct().ToArray() ?? [];
    }

    public CodeRepositoryId RepositoryId { get; }
    public CodeIndexRunId Id { get; }
    public DateTimeOffset StartedAt { get; }
    public CodeIndexRunStatus Status { get; }
    public DateTimeOffset? CompletedAt { get; }
    public IReadOnlyCollection<CodePluginId> Plugins { get; }
}

/// <summary>
/// Records the source inputs of the latest successfully completed repository index.
/// Index-unit ownership remains plugin-defined and is not inferred from these files.
/// </summary>
public sealed record CodeRepositoryIndexState
{
    public CodeRepositoryIndexState(
        CodeRepositoryId repositoryId,
        CodeIndexRunId indexRunId,
        IReadOnlyList<CodeSourceManifest> sources,
        string? snapshotIdentity = null,
        bool isConsistentSnapshot = false)
    {
        RepositoryId = repositoryId ?? throw new ArgumentNullException(nameof(repositoryId));
        IndexRunId = indexRunId ?? throw new ArgumentNullException(nameof(indexRunId));
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(source => source is null))
            throw new ArgumentException("Index state cannot contain null sources.", nameof(sources));

        Sources = sources
            .OrderBy(source => source.PluginId.Value, StringComparer.Ordinal)
            .ThenBy(source => source.SourcePath, StringComparer.Ordinal)
            .ToArray();
        if (Sources
            .GroupBy(source => $"{source.PluginId.Value}\n{source.SourcePath}", StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Index state cannot contain duplicate plugin and source-path pairs.",
                nameof(sources));
        }

        SnapshotIdentity = string.IsNullOrWhiteSpace(snapshotIdentity)
            ? null
            : snapshotIdentity;
        IsConsistentSnapshot = isConsistentSnapshot;
    }

    public CodeRepositoryId RepositoryId { get; }
    public CodeIndexRunId IndexRunId { get; }
    public IReadOnlyList<CodeSourceManifest> Sources { get; }
    public string? SnapshotIdentity { get; }
    public bool IsConsistentSnapshot { get; }
}

/// <summary>The complete new contribution of one atomically replaced index unit.</summary>
public sealed record CodeIndexUnitReplacement
{
    public CodeIndexUnitReplacement(
        CodeFactOrigin origin,
        IReadOnlyList<CodeGraphNode>? nodes = null,
        IReadOnlyList<CodeGraphDeclaration>? declarations = null,
        IReadOnlyList<CodeGraphEdge>? edges = null)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Nodes = nodes?.ToArray() ?? [];
        Declarations = declarations?.ToArray() ?? [];
        Edges = edges?.ToArray() ?? [];
        if (Nodes.Any(node => node is null) ||
            Declarations.Any(declaration => declaration is null) ||
            Edges.Any(edge => edge is null))
        {
            throw new ArgumentException(
                "Index-unit replacements cannot contain null facts.");
        }
    }

    public CodeFactOrigin Origin { get; }
    public IReadOnlyList<CodeGraphNode> Nodes { get; }
    public IReadOnlyList<CodeGraphDeclaration> Declarations { get; }
    public IReadOnlyList<CodeGraphEdge> Edges { get; }
}

public enum CodeGraphDirection
{
    Outgoing = 0,
    Incoming = 1,
    Both = 2
}

/// <summary>Bounds one deterministic graph traversal.</summary>
public sealed record CodeGraphTraversalQuery
{
    public CodeGraphTraversalQuery(
        CodeNodeId startNodeId,
        CodeGraphDirection direction = CodeGraphDirection.Outgoing,
        IReadOnlyCollection<CodeEdgeKind>? edgeKinds = null,
        IReadOnlyCollection<CodeEvidenceKind>? evidenceKinds = null,
        int maxDepth = 1,
        int maxNodes = 100,
        int maxEdges = 250)
    {
        StartNodeId = startNodeId ??
            throw new ArgumentNullException(nameof(startNodeId));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (maxEdges < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEdges));

        Direction = direction;
        EdgeKinds = edgeKinds?.Distinct().ToArray() ?? [];
        EvidenceKinds = evidenceKinds?.Distinct().ToArray() ?? [];
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        MaxEdges = maxEdges;
    }

    public CodeNodeId StartNodeId { get; }
    public CodeGraphDirection Direction { get; }
    public IReadOnlyCollection<CodeEdgeKind> EdgeKinds { get; }
    public IReadOnlyCollection<CodeEvidenceKind> EvidenceKinds { get; }
    public int MaxDepth { get; }
    public int MaxNodes { get; }
    public int MaxEdges { get; }
}

[Flags]
public enum CodeGraphTruncationReason
{
    None = 0,
    MaxDepth = 1,
    MaxNodes = 2,
    MaxEdges = 4
}

/// <summary>A bounded, deterministically ordered graph traversal result.</summary>
public sealed record CodeGraphTraversalResult
{
    public CodeGraphTraversalResult(
        IReadOnlyList<CodeGraphNode> nodes,
        IReadOnlyList<CodeGraphEdge> edges,
        bool truncated,
        CodeGraphTruncationReason truncationReason = CodeGraphTruncationReason.None,
        int depthReached = 0,
        int nodesExamined = 0,
        int edgesExamined = 0,
        int omittedFrontierCount = 0)
    {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        if (depthReached < 0 || nodesExamined < 0 || edgesExamined < 0 || omittedFrontierCount < 0)
            throw new ArgumentOutOfRangeException(nameof(depthReached));
        Truncated = truncated;
        TruncationReason = truncated ? truncationReason : CodeGraphTruncationReason.None;
        DepthReached = depthReached;
        NodesExamined = nodesExamined;
        EdgesExamined = edgesExamined;
        OmittedFrontierCount = omittedFrontierCount;
    }

    public IReadOnlyList<CodeGraphNode> Nodes { get; }
    public IReadOnlyList<CodeGraphEdge> Edges { get; }
    public bool Truncated { get; }
    public CodeGraphTruncationReason TruncationReason { get; }
    public int DepthReached { get; }
    public int NodesExamined { get; }
    public int EdgesExamined { get; }
    public int OmittedFrontierCount { get; }
}

/// <summary>Identifies the successful repository publication observed by a query.</summary>
public sealed record CodeGraphPublication(
    CodeRepositoryId RepositoryId,
    CodeIndexRunId IndexRunId,
    string? SnapshotIdentity,
    bool IsConsistentSnapshot);

public enum CodeGraphFactKind
{
    Node = 0,
    Declaration = 1,
    Edge = 2
}

/// <summary>Traces one returned fact to every index unit that contributed it.</summary>
public sealed record CodeGraphFactProvenance(
    CodeGraphFactKind Kind,
    string FactId,
    IReadOnlyList<CodeFactOrigin> Contributors);

/// <summary>Binds a query result and its contributors to one atomic publication.</summary>
public sealed record CodeGraphQueryDescriptor(
    string Operation,
    string? QualifiedName = null,
    CodeGraphTraversalQuery? Traversal = null);

/// <summary>Binds a query result and its contributors to one atomic publication.</summary>
public sealed record CodeGraphQueryEnvelope<TResult>(
    CodeGraphPublication Publication,
    CodeGraphQueryDescriptor Query,
    TResult Result,
    IReadOnlyList<CodeGraphFactProvenance> Provenance);

/// <summary>Privacy-safe counters from one completed index-unit ingestion.</summary>
public sealed record CodeGraphIngestionDiagnostics(
    CodeRepositoryId RepositoryId,
    CodeIndexRunId IndexRunId,
    CodeIndexUnitId IndexUnitId,
    int BatchesReceived,
    int NodesReceived,
    int DeclarationsReceived,
    int EdgesReceived,
    int RejectedFacts,
    IReadOnlyList<string> WarningCodes);
