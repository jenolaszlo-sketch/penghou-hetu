namespace Penghou.Hetu;

/// <summary>Identifies one logical repository across checkouts and index runs.</summary>
public sealed record CodeRepositoryId
{
    public CodeRepositoryId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies one extraction plugin independently of its version.</summary>
public sealed record CodePluginId
{
    public CodePluginId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies an atomically replaceable unit of extracted facts.</summary>
public sealed record CodeIndexUnitId
{
    public CodeIndexUnitId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies one indexing attempt.</summary>
public sealed record CodeIndexRunId
{
    public CodeIndexRunId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies a normalized graph node within a repository.</summary>
public sealed record CodeNodeId
{
    public CodeNodeId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies a semantic language symbol within a repository.</summary>
public sealed record CodeSymbolId
{
    public CodeSymbolId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies one physical declaration of a semantic symbol.</summary>
public sealed record CodeDeclarationId
{
    public CodeDeclarationId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Identifies one normalized relationship.</summary>
public sealed record CodeEdgeId
{
    public CodeEdgeId(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}
