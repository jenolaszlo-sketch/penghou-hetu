namespace Penghou.Hetu.Testing;

/// <summary>Lists the provider-neutral graph-store laws that passed.</summary>
public sealed record CodeGraphStoreConformanceReport(
    IReadOnlyList<string> PassedChecks);

/// <summary>Reports a provider violation found by the conformance suite.</summary>
public sealed class CodeGraphStoreConformanceException(string message)
    : Exception(message);
