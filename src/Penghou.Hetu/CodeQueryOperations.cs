namespace Penghou.Hetu;

/// <summary>
/// Stable operation names carried by query envelopes. Services and stores
/// use these instead of repeating literals so telemetry and ranking can rely
/// on one vocabulary.
/// </summary>
public static class CodeQueryOperations
{
    public const string QualifiedName = "qualified-name";
    public const string Traversal = "traversal";
    public const string Declarations = "declarations";
    public const string ResolveSymbols = "resolve-symbols";
    public const string DeclarationsInFile = "declarations-in-file";
    public const string PublicSurface = "public-surface";
    public const string ImpactSets = "impact-sets";
    public const string AffectedTests = "affected-tests";
}
