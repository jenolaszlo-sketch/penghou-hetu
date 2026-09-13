using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Penghou.Hetu;

public sealed partial class CSharpCodeGraphPlugin
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences =
        new(BuildPlatformReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    private static CodeLocation Location(string path, SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan().Span;
        return new(
            path,
            span.Start.Line + 1,
            span.Start.Character + 1,
            span.End.Line + 1,
            span.End.Character + 1);
    }

    private static CSharpParseOptions CreateParseOptions(CSharpProjectModel project)
    {
        var languageVersion = LanguageVersion.Latest;
        if (!string.IsNullOrWhiteSpace(project.LanguageVersion) &&
            LanguageVersionFacts.TryParse(project.LanguageVersion, out var parsed))
        {
            languageVersion = parsed;
        }
        return new CSharpParseOptions(
            languageVersion,
            documentationMode: DocumentationMode.Diagnose,
            preprocessorSymbols: project.DefineConstants);
    }

    private static CSharpCompilationOptions CreateCompilationOptions(CSharpProjectModel project)
    {
        var nullable = project.Nullable?.ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            _ => NullableContextOptions.Disable
        };
        return new(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: nullable,
            deterministic: true,
            concurrentBuild: false);
    }

    private static IReadOnlyList<MetadataReference> CreatePlatformReferences() => PlatformReferences.Value;

    private static IReadOnlyList<MetadataReference> BuildPlatformReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        }

        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
