using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Penghou.Hetu;

internal sealed record CSharpProjectModel(
    string Path,
    string Name,
    string AssemblyName,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> ProjectReferences,
    string? TargetFramework,
    string? LanguageVersion,
    string? Nullable,
    IReadOnlyList<string> DefineConstants,
    bool ImplicitUsings,
    IReadOnlyList<string> WarningCodes)
{
    public string Directory => Path.Contains('/')
        ? Path[..Path.LastIndexOf('/')]
        : string.Empty;
}

internal static partial class CSharpProjectDiscovery
{
    private const string LooseProjectPath = "@loose/csharp";

    public static IReadOnlyList<CSharpProjectModel> Discover(
        IReadOnlyDictionary<string, string> content)
    {
        var sourcePaths = content.Keys
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var projectPaths = content.Keys
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (projectPaths.Length == 0)
            return [Loose(sourcePaths)];

        var projects = projectPaths
            .Select(path => Parse(path, content[path], sourcePaths))
            .ToList();
        var assigned = projects
            .SelectMany(project => project.SourcePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = sourcePaths.Where(path =>
            !assigned.Contains(path) &&
            !projects.Any(project => IsUnderDirectory(path, project.Directory))).ToArray();
        if (loose.Length > 0)
            projects.Add(Loose(loose));
        return projects.OrderBy(project => project.Path, StringComparer.Ordinal).ToArray();
    }

    public static string IndexUnitId(string projectPath) =>
        $"csharp:project:{IdentityHash(projectPath)}";

    private static CSharpProjectModel Parse(
        string path,
        string xml,
        IReadOnlyList<string> repositorySources)
    {
        var warnings = new List<string>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch
        {
            warnings.Add("csharp.project.invalid-xml");
            document = new XDocument(new XElement("Project"));
        }

        var directory = Directory(path);
        string? Property(string name) => document.Descendants()
            .Where(element => element.Name.LocalName == name)
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => value.Length > 0);
        var defaultItems = !string.Equals(
            Property("EnableDefaultCompileItems"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        var includes = Attributes(document, "Compile", "Include").ToArray();
        var removes = Attributes(document, "Compile", "Remove").ToArray();
        var selected = repositorySources.Where(source =>
            defaultItems && IsUnderDirectory(source, directory) ||
            includes.Any(pattern => Matches(source, directory, pattern)));
        selected = selected.Where(source =>
            !removes.Any(pattern => Matches(source, directory, pattern)));
        var references = Attributes(document, "ProjectReference", "Include")
            .Select(reference => Combine(directory, reference))
            .Where(reference => reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var constants = (Property("DefineConstants") ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new(
            path,
            name,
            Property("AssemblyName") ?? name,
            selected.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            references,
            Property("TargetFramework") ?? Property("TargetFrameworks")?.Split(';')[0],
            Property("LangVersion"),
            Property("Nullable"),
            constants,
            string.Equals(Property("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase),
            warnings);
    }

    private static CSharpProjectModel Loose(IReadOnlyList<string> sources) =>
        new(
            LooseProjectPath,
            "Loose C# Sources",
            "Hetu.Loose.CSharp",
            sources,
            [],
            null,
            null,
            null,
            [],
            false,
            []);

    private static IEnumerable<string> Attributes(
        XDocument document,
        string elementName,
        string attributeName) => document.Descendants()
        .Where(element => element.Name.LocalName == elementName)
        .Select(element => element.Attribute(attributeName)?.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool Matches(string source, string directory, string pattern)
    {
        var combined = Combine(directory, pattern.Replace('\\', '/'));
        var regex = "^" + Regex.Escape(combined)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(source, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsUnderDirectory(string path, string directory) =>
        directory.Length == 0 ||
        path.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase);

    private static string Directory(string path) => path.Contains('/')
        ? path[..path.LastIndexOf('/')]
        : string.Empty;

    private static string Combine(string directory, string value)
    {
        var segments = new List<string>();
        foreach (var segment in $"{directory}/{value}".Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string IdentityHash(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));
}
