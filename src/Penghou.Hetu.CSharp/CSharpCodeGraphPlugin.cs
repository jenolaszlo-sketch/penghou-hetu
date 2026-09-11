using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Penghou.Hetu;

/// <summary>Deterministic repository-aware C# extraction powered by Roslyn.</summary>
public sealed partial class CSharpCodeGraphPlugin : ICodeGraphPlugin
{
    private static readonly HashSet<string> UnresolvedDiagnosticIds =
        new(StringComparer.Ordinal)
        {
            "CS0012", // referenced assembly is missing
            "CS0103", // name does not exist in the current context
            "CS0234", // namespace member is missing
            "CS0246", // type or namespace cannot be found
            "CS0400", // type or namespace cannot be found in the global namespace
            "CS0426", // nested type does not exist
            "CS0518", // predefined type is not defined or imported
            "CS1061"  // member or extension method cannot be found
        };
    private static readonly string PackageVersion = GetPackageVersion();

    public CodePluginId Id { get; } = new("penghou.hetu.csharp");
    public string Version => PackageVersion;
    public string Language => "csharp";
    public IReadOnlyCollection<string> FileExtensions =>
        [".cs", ".csproj", ".sln", ".props", ".targets"];
    public CodeGraphCapabilities Capabilities =>
        CodeGraphCapabilities.Syntax |
        CodeGraphCapabilities.Symbols |
        CodeGraphCapabilities.Types;

    public bool CanHandle(string path) =>
        FileExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public ValueTask<ICodeGraphExtractionSession> CreateSessionAsync(
        CodeGraphPluginContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return new(new Session(context, this));
    }

    private static string GetPackageVersion()
    {
        var assembly = typeof(CSharpCodeGraphPlugin).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
