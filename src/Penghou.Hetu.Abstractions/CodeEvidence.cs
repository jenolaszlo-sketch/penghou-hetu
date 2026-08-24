namespace Penghou.Hetu;

public enum CodeEvidenceKind
{
    Syntax = 0,
    Semantic = 1,
    Heuristic = 2,
    AiInferred = 3
}

/// <summary>Describes how a graph relationship was discovered.</summary>
public sealed record CodeEvidence
{
    public CodeEvidence(
        CodeEvidenceKind kind,
        string provider,
        string? providerVersion = null,
        CodeLocation? location = null,
        double? confidence = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (confidence is < 0 or > 1 ||
            confidence is { } score && !double.IsFinite(score))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        if (confidence is not null &&
            kind is CodeEvidenceKind.Syntax or CodeEvidenceKind.Semantic)
        {
            throw new ArgumentException(
                "Deterministic syntax and semantic evidence cannot declare confidence.",
                nameof(confidence));
        }

        Kind = kind;
        Provider = ContractValue.Identifier(provider, nameof(provider));
        ProviderVersion = string.IsNullOrWhiteSpace(providerVersion)
            ? null
            : providerVersion;
        Location = location;
        Confidence = confidence;
    }

    public CodeEvidenceKind Kind { get; }
    public string Provider { get; }
    public string? ProviderVersion { get; }
    public CodeLocation? Location { get; }
    public double? Confidence { get; }
}
