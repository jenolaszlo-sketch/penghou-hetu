using System.Text.Json;
using LatticeDbSharp;
using static Penghou.Hetu.DurableCommandLog;

namespace Penghou.Hetu;

/// <summary>
/// An experimental native-graph mirror of published facts for
/// <see cref="LatticeCodeGraphStore"/>.
///
/// The mirror is derived state, never the source of truth: the command log
/// stays authoritative for staging, publication, and recovery, while the
/// mirror serves <c>TraverseAsync</c> reads natively. It is rebuilt from
/// published units on every publication and reopen, so a corrupt or stale
/// mirror is always recoverable by rebuilding. Provenance-bound queries and
/// every other read stay on the materialized projection.
/// </summary>
internal sealed class NativeGraphMirror
{
    internal const string NodeLabel = "HetuGraphNode";
    internal const string MirrorLabel = "HetuMirror";
    private const string JsonProperty = "hetu_json";

    private readonly MirrorManifest manifest;
    private readonly Dictionary<ulong, string> nodeByNative;
    private readonly Dictionary<ulong, string> edgeByNative;

    internal NativeGraphMirror(MirrorManifest manifest)
    {
        this.manifest = manifest;
        nodeByNative = manifest.Nodes.ToDictionary(
            pair => pair.Value, pair => pair.Key);
        edgeByNative = manifest.Edges.ToDictionary(
            pair => pair.Value, pair => pair.Key);
    }

    internal MirrorManifest Manifest => manifest;

    internal static string WriteManifest(MirrorManifest manifest) =>
        JsonSerializer.Serialize(manifest, SerializerOptions);

    internal static MirrorManifest ReadManifest(string payload) =>
        JsonSerializer.Deserialize<MirrorManifest>(payload, SerializerOptions)
        ?? new(new Dictionary<string, ulong>(), new Dictionary<string, ulong>());

    /// <summary>
    /// Replaces the repository's mirror with the given published units,
    /// deleting previously mirrored facts first. Runs inside the caller's
    /// transaction.
    /// </summary>
    internal static NativeGraphMirror Rebuild(
        LatticeTransaction transaction,
        IReadOnlyList<CodeIndexUnitReplacement> units,
        MirrorManifest? existing)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(units);
        if (existing is not null)
        {
            var deletedEdges = new HashSet<ulong>();
            foreach (var nativeId in existing.Nodes.Values)
            {
                var node = new LatticeNodeId(nativeId);
                foreach (var edge in transaction.GetOutgoingEdges(node)
                    .Concat(transaction.GetIncomingEdges(node)))
                {
                    if (deletedEdges.Add(edge.Id.Value))
                    {
                        transaction.DeleteEdge(edge.Source, edge.Target, edge.Type);
                    }
                }

                transaction.DeleteNode(node);
            }
        }

        var nodes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var edges = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            foreach (var node in unit.Nodes)
            {
                var id = transaction.CreateNode(NodeLabel);
                transaction.SetProperty(
                    id,
                    JsonProperty,
                    LatticeValue.From(JsonSerializer.Serialize(node, SerializerOptions)));
                nodes[node.Id.Value] = id.Value;
            }
        }

        foreach (var unit in units)
        {
            foreach (var edge in unit.Edges)
            {
                if (!nodes.TryGetValue(edge.SourceId.Value, out var source) ||
                    !nodes.TryGetValue(edge.TargetId.Value, out var target))
                {
                    throw new InvalidDataException(
                        $"Published edge '{edge.Id.Value}' references a missing mirrored node.");
                }

                var id = transaction.CreateEdge(
                    new LatticeNodeId(source),
                    new LatticeNodeId(target),
                    edge.Kind.Value);
                transaction.SetEdgeProperty(
                    id,
                    JsonProperty,
                    LatticeValue.From(JsonSerializer.Serialize(edge, SerializerOptions)));
                edges[edge.Id.Value] = id.Value;
            }
        }

        return new NativeGraphMirror(new MirrorManifest(nodes, edges));
    }

    /// <summary>
    /// Runs a bounded traversal against native adjacency with the same
    /// bounds, ordering, and diagnostics as the materialized projection.
    /// The caller must route evidence-filtered queries to the projection:
    /// evidence lives in edge facts, not in traversal results.
    /// </summary>
    internal CodeGraphTraversalResult Traverse(
        LatticeTransaction transaction,
        CodeGraphTraversalQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(query);
        if (query.EvidenceKinds.Count != 0)
        {
            throw new ArgumentException(
                "Native traversal cannot filter by evidence kind.",
                nameof(query));
        }

        var selectedKinds = query.EdgeKinds
            .Select(kind => kind.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (!manifest.Nodes.TryGetValue(query.StartNodeId.Value, out var startNative))
        {
            return new([], [], false);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            query.StartNodeId.Value
        };
        var nodeIds = new List<string> { query.StartNodeId.Value };
        var edgeIds = new List<string>();
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string HetuId, ulong NativeId, int Depth)>();
        queue.Enqueue((query.StartNodeId.Value, startNative, 0));
        var truncated = false;
        var truncationReason = CodeGraphTruncationReason.None;
        var depthReached = 0;
        var nodesExamined = 0;
        var edgesExamined = 0;
        var omittedFrontierCount = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (hetuId, nativeId, depth) = queue.Dequeue();
            nodesExamined++;
            depthReached = Math.Max(depthReached, depth);
            if (depth >= query.MaxDepth)
            {
                var omitted = Adjacent(transaction, nativeId, query.Direction)
                    .DistinctBy(edge => edge.NativeId)
                    .Count(edge =>
                        selectedKinds.Count == 0 || selectedKinds.Contains(edge.Kind));
                if (omitted > 0)
                {
                    truncated = true;
                    truncationReason |= CodeGraphTruncationReason.MaxDepth;
                    omittedFrontierCount += omitted;
                }

                continue;
            }

            var candidates = Adjacent(transaction, nativeId, query.Direction)
                .Where(edge => selectedKinds.Count == 0 ||
                    selectedKinds.Contains(edge.Kind))
                .DistinctBy(edge => edge.NativeId)
                .OrderBy(edge => edge.Kind, StringComparer.Ordinal)
                .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
                .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal);

            foreach (var edge in candidates)
            {
                edgesExamined++;
                var adjacent = edge.SourceId == hetuId
                    ? edge.TargetId
                    : edge.SourceId;
                var adjacentNative = edge.SourceId == hetuId
                    ? edge.TargetNative
                    : edge.SourceNative;
                var isNewNode = !visited.Contains(adjacent);
                if (isNewNode && nodeIds.Count >= query.MaxNodes)
                {
                    truncated = true;
                    truncationReason |= CodeGraphTruncationReason.MaxNodes;
                    omittedFrontierCount++;
                    continue;
                }

                if (seenEdges.Add(edge.EdgeId))
                {
                    if (edgeIds.Count >= query.MaxEdges)
                    {
                        truncated = true;
                        truncationReason |= CodeGraphTruncationReason.MaxEdges;
                        break;
                    }

                    edgeIds.Add(edge.EdgeId);
                }

                if (!isNewNode)
                    continue;

                visited.Add(adjacent);
                nodeIds.Add(adjacent);
                queue.Enqueue((adjacent, adjacentNative, depth + 1));
            }

            if (truncated && edgeIds.Count >= query.MaxEdges)
                break;
        }

        return new CodeGraphTraversalResult(
            nodeIds.Select(id => ReadNode(transaction, id)).ToArray(),
            edgeIds.Select(id => ReadEdge(transaction, id)).ToArray(),
            truncated,
            truncationReason,
            depthReached,
            nodesExamined,
            edgesExamined,
            omittedFrontierCount);
    }

    private IReadOnlyList<NativeAdjacency> Adjacent(
        LatticeTransaction transaction,
        ulong nativeId,
        CodeGraphDirection direction)
    {
        var node = new LatticeNodeId(nativeId);
        IEnumerable<LatticeEdgeInfo> edges = direction switch
        {
            CodeGraphDirection.Outgoing => transaction.GetOutgoingEdges(node),
            CodeGraphDirection.Incoming => transaction.GetIncomingEdges(node),
            CodeGraphDirection.Both => transaction.GetOutgoingEdges(node)
                .Concat(transaction.GetIncomingEdges(node)),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        return edges.Select(edge => ResolveEdge(edge)).ToArray();
    }

    private NativeAdjacency ResolveEdge(LatticeEdgeInfo edge)
    {
        if (!edgeByNative.TryGetValue(edge.Id.Value, out var hetuEdge) ||
            !nodeByNative.TryGetValue(edge.Source.Value, out var sourceHetu) ||
            !nodeByNative.TryGetValue(edge.Target.Value, out var targetHetu))
        {
            throw new InvalidDataException(
                "The native mirror references an unmapped fact.");
        }

        return new NativeAdjacency(
            edge.Id.Value,
            edge.Type,
            edge.Source.Value,
            edge.Target.Value,
            hetuEdge,
            sourceHetu,
            targetHetu);
    }

    private CodeGraphNode ReadNode(LatticeTransaction transaction, string hetuId)
    {
        if (!manifest.Nodes.TryGetValue(hetuId, out var nativeId) ||
            !transaction.TryGetProperty(
                new LatticeNodeId(nativeId),
                JsonProperty,
                out var value) ||
            JsonSerializer.Deserialize<CodeGraphNode>(value.AsString(), SerializerOptions) is not { } node)
        {
            throw new InvalidDataException(
                $"The native mirror is missing node '{hetuId}'.");
        }

        return node;
    }

    private CodeGraphEdge ReadEdge(LatticeTransaction transaction, string hetuId)
    {
        if (!manifest.Edges.TryGetValue(hetuId, out var nativeId) ||
            !transaction.TryGetEdgeProperty(
                new LatticeEdgeId(nativeId),
                JsonProperty,
                out var value) ||
            JsonSerializer.Deserialize<CodeGraphEdge>(value.AsString(), SerializerOptions) is not { } edge)
        {
            throw new InvalidDataException(
                $"The native mirror is missing edge '{hetuId}'.");
        }

        return edge;
    }

    private sealed record NativeAdjacency(
        ulong NativeId,
        string Kind,
        ulong SourceNative,
        ulong TargetNative,
        string EdgeId,
        string SourceId,
        string TargetId);

    internal sealed record MirrorManifest(
        Dictionary<string, ulong> Nodes,
        Dictionary<string, ulong> Edges);
}

