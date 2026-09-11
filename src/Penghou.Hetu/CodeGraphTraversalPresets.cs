namespace Penghou.Hetu;

/// <summary>
/// Shared edge-kind sets for the query service and publication-bound views.
/// Query authors use these instead of repeating literals so both surfaces
/// stay in sync when a relationship kind is added.
/// </summary>
internal static class CodeGraphTraversalPresets
{
    internal static readonly IReadOnlyList<CodeEdgeKind> References = [CodeEdgeKinds.References];
    internal static readonly IReadOnlyList<CodeEdgeKind> Calls = [CodeEdgeKinds.Calls];
    internal static readonly IReadOnlyList<CodeEdgeKind> Implementations = [CodeEdgeKinds.Implements, CodeEdgeKinds.Inherits];
    internal static readonly IReadOnlyList<CodeEdgeKind> Dependencies = [CodeEdgeKinds.DependsOn];
    internal static readonly IReadOnlyList<CodeEdgeKind> Declarations = [CodeEdgeKinds.Declares];
    internal static readonly IReadOnlyList<CodeEdgeKind> Containment = [CodeEdgeKinds.Contains, CodeEdgeKinds.Declares];
    internal static readonly IReadOnlyList<CodeEdgeKind> ImpactSet =
    [
        CodeEdgeKinds.References,
        CodeEdgeKinds.Calls,
        CodeEdgeKinds.Implements,
        CodeEdgeKinds.Inherits,
        CodeEdgeKinds.DependsOn
    ];
}
