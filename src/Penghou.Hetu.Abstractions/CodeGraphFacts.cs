namespace Penghou.Hetu;

/// <summary>A normalized semantic or structural entity.</summary>
public sealed record CodeGraphNode
{
    public CodeGraphNode(
        CodeNodeId id,
        CodeNodeKind kind,
        string name,
        string? qualifiedName = null,
        CodeSymbolId? symbolId = null,
        IReadOnlyDictionary<string, CodePropertyValue>? properties = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Name = ContractValue.Required(name, nameof(name));
        QualifiedName = string.IsNullOrWhiteSpace(qualifiedName)
            ? null
            : qualifiedName;
        SymbolId = symbolId;
        Properties = CopyProperties(properties);
    }

    public CodeNodeId Id { get; }
    public CodeNodeKind Kind { get; }
    public string Name { get; }
    public string? QualifiedName { get; }
    public CodeSymbolId? SymbolId { get; }
    public IReadOnlyDictionary<string, CodePropertyValue> Properties { get; }

    internal static IReadOnlyDictionary<string, CodePropertyValue> CopyProperties(
        IReadOnlyDictionary<string, CodePropertyValue>? properties)
    {
        if (properties is null)
            return new Dictionary<string, CodePropertyValue>(StringComparer.Ordinal);

        var copy = new SortedDictionary<string, CodePropertyValue>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            copy.Add(
                ContractValue.Identifier(key, nameof(properties)),
                value ?? throw new ArgumentException(
                    "Graph properties cannot contain null values.",
                    nameof(properties)));
        }

        return copy;
    }
}

/// <summary>One physical declaration of a semantic symbol.</summary>
public sealed record CodeGraphDeclaration
{
    public CodeGraphDeclaration(
        CodeDeclarationId id,
        CodeSymbolId symbolId,
        CodeNodeId symbolNodeId,
        CodeLocation location,
        IReadOnlyDictionary<string, CodePropertyValue>? properties = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        SymbolNodeId = symbolNodeId ??
            throw new ArgumentNullException(nameof(symbolNodeId));
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Properties = CodeGraphNode.CopyProperties(properties);
    }

    public CodeDeclarationId Id { get; }
    public CodeSymbolId SymbolId { get; }
    public CodeNodeId SymbolNodeId { get; }
    public CodeLocation Location { get; }
    public IReadOnlyDictionary<string, CodePropertyValue> Properties { get; }
}

/// <summary>A normalized directed relationship.</summary>
public sealed record CodeGraphEdge
{
    public CodeGraphEdge(
        CodeEdgeId id,
        CodeNodeId sourceId,
        CodeNodeId targetId,
        CodeEdgeKind kind,
        CodeEvidence evidence,
        IReadOnlyDictionary<string, CodePropertyValue>? properties = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        SourceId = sourceId ?? throw new ArgumentNullException(nameof(sourceId));
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Properties = CodeGraphNode.CopyProperties(properties);
    }

    public CodeEdgeId Id { get; }
    public CodeNodeId SourceId { get; }
    public CodeNodeId TargetId { get; }
    public CodeEdgeKind Kind { get; }
    public CodeEvidence Evidence { get; }
    public IReadOnlyDictionary<string, CodePropertyValue> Properties { get; }
}
