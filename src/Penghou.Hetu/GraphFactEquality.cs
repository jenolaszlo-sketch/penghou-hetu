namespace Penghou.Hetu;

internal static class GraphFactEquality
{
    public static bool Equivalent(CodeGraphNode left, CodeGraphNode right) =>
        left.Id == right.Id &&
        left.Kind == right.Kind &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(
            left.QualifiedName,
            right.QualifiedName,
            StringComparison.Ordinal) &&
        left.SymbolId == right.SymbolId &&
        PropertiesEqual(left.Properties, right.Properties);

    public static bool Equivalent(
        CodeGraphDeclaration left,
        CodeGraphDeclaration right) =>
        left.Id == right.Id &&
        left.SymbolId == right.SymbolId &&
        left.SymbolNodeId == right.SymbolNodeId &&
        left.Location == right.Location &&
        PropertiesEqual(left.Properties, right.Properties);

    public static bool Equivalent(CodeGraphEdge left, CodeGraphEdge right) =>
        left.Id == right.Id &&
        left.SourceId == right.SourceId &&
        left.TargetId == right.TargetId &&
        left.Kind == right.Kind &&
        left.Evidence == right.Evidence &&
        PropertiesEqual(left.Properties, right.Properties);

    private static bool PropertiesEqual(
        IReadOnlyDictionary<string, CodePropertyValue> left,
        IReadOnlyDictionary<string, CodePropertyValue> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) ||
                !PropertyEqual(leftValue, rightValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PropertyEqual(
        CodePropertyValue left,
        CodePropertyValue right) =>
        (left, right) switch
        {
            (CodeTextProperty first, CodeTextProperty second) =>
                first.Value == second.Value,
            (CodeIntegerProperty first, CodeIntegerProperty second) =>
                first.Value == second.Value,
            (CodeNumberProperty first, CodeNumberProperty second) =>
                first.Value.Equals(second.Value),
            (CodeBooleanProperty first, CodeBooleanProperty second) =>
                first.Value == second.Value,
            (CodeTextListProperty first, CodeTextListProperty second) =>
                first.Values.SequenceEqual(second.Values, StringComparer.Ordinal),
            _ => false
        };
}
