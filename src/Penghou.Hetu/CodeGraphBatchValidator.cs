namespace Penghou.Hetu;

/// <summary>Validates bounded graph batches before they reach persistence.</summary>
public sealed class CodeGraphBatchValidator
{
    private const int MaxReportedErrors = 100;

    public IReadOnlyList<CodeGraphValidationError> Validate(
        CodeGraphBatch batch,
        CodeGraphBatchLimits limits)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new List<CodeGraphValidationError>();
        CheckLimit(batch.Nodes.Count, limits.MaxNodes, "nodes", errors);
        CheckLimit(
            batch.Declarations.Count,
            limits.MaxDeclarations,
            "declarations",
            errors);
        CheckLimit(batch.Edges.Count, limits.MaxEdges, "edges", errors);
        CheckDuplicates(batch.Nodes.Select(node => node.Id.Value), "node", errors);
        CheckDuplicates(
            batch.Declarations.Select(declaration => declaration.Id.Value),
            "declaration",
            errors);
        CheckDuplicates(batch.Edges.Select(edge => edge.Id.Value), "edge", errors);

        foreach (var node in batch.Nodes)
            CheckProperties(node.Id.Value, node.Properties, limits, errors);
        foreach (var declaration in batch.Declarations)
        {
            CheckProperties(
                declaration.Id.Value,
                declaration.Properties,
                limits,
                errors);
        }
        foreach (var edge in batch.Edges)
            CheckProperties(edge.Id.Value, edge.Properties, limits, errors);

        return errors.Take(MaxReportedErrors).ToArray();
    }

    private static void CheckLimit(
        int actual,
        int maximum,
        string factKind,
        ICollection<CodeGraphValidationError> errors)
    {
        if (actual <= maximum)
            return;

        errors.Add(new CodeGraphValidationError(
            CodeGraphValidationErrorKind.LimitExceeded,
            $"batch.{factKind}.limit",
            $"Batch {factKind} count {actual} exceeds limit {maximum}."));
    }

    private static void CheckDuplicates(
        IEnumerable<string> ids,
        string factKind,
        ICollection<CodeGraphValidationError> errors)
    {
        foreach (var duplicate in ids
                     .GroupBy(id => id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add(new CodeGraphValidationError(
                CodeGraphValidationErrorKind.DuplicateFact,
                $"batch.{factKind}.duplicate",
                $"Batch contains duplicate {factKind} identity.",
                duplicate));
        }
    }

    private static void CheckProperties(
        string factId,
        IReadOnlyDictionary<string, CodePropertyValue> properties,
        CodeGraphBatchLimits limits,
        ICollection<CodeGraphValidationError> errors)
    {
        if (properties.Count > limits.MaxPropertiesPerFact)
        {
            errors.Add(new CodeGraphValidationError(
                CodeGraphValidationErrorKind.LimitExceeded,
                "batch.properties.limit",
                $"Fact property count {properties.Count} exceeds limit {limits.MaxPropertiesPerFact}.",
                factId));
        }

        foreach (var property in properties.Values)
        {
            switch (property)
            {
                case CodeTextProperty text when
                    text.Value.Length > limits.MaxTextPropertyLength:
                    errors.Add(new CodeGraphValidationError(
                        CodeGraphValidationErrorKind.LimitExceeded,
                        "batch.property.text-length",
                        "Text property exceeds the configured length limit.",
                        factId));
                    break;
                case CodeTextListProperty list when
                    list.Values.Count > limits.MaxTextListItems:
                    errors.Add(new CodeGraphValidationError(
                        CodeGraphValidationErrorKind.LimitExceeded,
                        "batch.property.list-items",
                        "Text-list property exceeds the configured item limit.",
                        factId));
                    break;
                case CodeTextListProperty list when
                    list.Values.Any(value =>
                        value.Length > limits.MaxTextPropertyLength):
                    errors.Add(new CodeGraphValidationError(
                        CodeGraphValidationErrorKind.LimitExceeded,
                        "batch.property.list-text-length",
                        "Text-list item exceeds the configured length limit.",
                        factId));
                    break;
            }
        }
    }
}
