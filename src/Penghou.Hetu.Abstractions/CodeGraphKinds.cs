namespace Penghou.Hetu;

/// <summary>An extensible normalized node classification.</summary>
public sealed record CodeNodeKind
{
    public CodeNodeKind(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Well-known normalized node kinds.</summary>
public static class CodeNodeKinds
{
    public static readonly CodeNodeKind Repository = new("repository");
    public static readonly CodeNodeKind Project = new("project");
    public static readonly CodeNodeKind Package = new("package");
    public static readonly CodeNodeKind File = new("file");
    public static readonly CodeNodeKind Namespace = new("namespace");
    public static readonly CodeNodeKind Type = new("type");
    public static readonly CodeNodeKind Interface = new("interface");
    public static readonly CodeNodeKind Callable = new("callable");
    public static readonly CodeNodeKind Property = new("property");
    public static readonly CodeNodeKind Field = new("field");
    public static readonly CodeNodeKind Parameter = new("parameter");
}

/// <summary>An extensible normalized edge classification.</summary>
public sealed record CodeEdgeKind
{
    public CodeEdgeKind(string value) =>
        Value = ContractValue.Identifier(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Well-known normalized edge kinds.</summary>
public static class CodeEdgeKinds
{
    public static readonly CodeEdgeKind Contains = new("contains");
    public static readonly CodeEdgeKind Declares = new("declares");
    public static readonly CodeEdgeKind References = new("references");
    public static readonly CodeEdgeKind Calls = new("calls");
    public static readonly CodeEdgeKind Implements = new("implements");
    public static readonly CodeEdgeKind Inherits = new("inherits");
    public static readonly CodeEdgeKind Imports = new("imports");
    public static readonly CodeEdgeKind DependsOn = new("depends-on");
    public static readonly CodeEdgeKind Returns = new("returns");
    public static readonly CodeEdgeKind Accepts = new("accepts");
}
