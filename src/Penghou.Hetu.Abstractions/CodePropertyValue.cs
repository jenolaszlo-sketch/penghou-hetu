using System.Text.Json.Serialization;

namespace Penghou.Hetu;

/// <summary>A deterministic, portable value attached to a normalized graph fact.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CodeTextProperty), "text")]
[JsonDerivedType(typeof(CodeIntegerProperty), "integer")]
[JsonDerivedType(typeof(CodeNumberProperty), "number")]
[JsonDerivedType(typeof(CodeBooleanProperty), "boolean")]
[JsonDerivedType(typeof(CodeTextListProperty), "text-list")]
public abstract record CodePropertyValue;

public sealed record CodeTextProperty : CodePropertyValue
{
    [JsonConstructor]
    public CodeTextProperty(string value) =>
        Value = value ?? throw new ArgumentNullException(nameof(value));

    public string Value { get; }
}

public sealed record CodeIntegerProperty(long Value) : CodePropertyValue;

public sealed record CodeNumberProperty : CodePropertyValue
{
    [JsonConstructor]
    public CodeNumberProperty(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Graph numbers must be finite.");
        }

        Value = value;
    }

    public double Value { get; }
}

public sealed record CodeBooleanProperty(bool Value) : CodePropertyValue;

public sealed record CodeTextListProperty : CodePropertyValue
{
    [JsonConstructor]
    public CodeTextListProperty(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null))
        {
            throw new ArgumentException(
                "Text lists cannot contain null values.",
                nameof(values));
        }

        Values = values.ToArray();
    }

    public IReadOnlyList<string> Values { get; }
}
