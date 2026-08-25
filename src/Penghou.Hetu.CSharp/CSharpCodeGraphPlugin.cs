using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Penghou.Hetu;

/// <summary>Deterministic repository-aware C# extraction powered by Roslyn.</summary>
public sealed class CSharpCodeGraphPlugin : ICodeGraphPlugin
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

    private sealed class Session(
        CodeGraphPluginContext context,
        CSharpCodeGraphPlugin plugin) : ICodeGraphExtractionSession
    {
        public async ValueTask<CodeGraphExtractionResult> ExtractAsync(
            ICodeGraphSink sink,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sink);
            var content = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in context.Sources.OrderBy(source => source.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                content.Add(
                    source.Path,
                    await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }

            var projects = CSharpProjectDiscovery.Discover(content);
            var projectByPath = projects.ToDictionary(
                project => project.Path,
                StringComparer.OrdinalIgnoreCase);
            var compilations = new Dictionary<string, CSharpCompilation>(
                StringComparer.OrdinalIgnoreCase);
            var allDiagnostics = new List<Diagnostic>();
            var warningCodes = new HashSet<string>(StringComparer.Ordinal);
            var contributingSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in OrderProjects(projects, projectByPath, warningCodes))
            {
                var parseOptions = CreateParseOptions(project);
                var trees = project.SourcePaths
                    .Where(content.ContainsKey)
                    .Select(path => CSharpSyntaxTree.ParseText(
                        content[path],
                        parseOptions,
                        path,
                        cancellationToken: cancellationToken))
                    .ToArray();
                var references = CreatePlatformReferences().ToList();
                var availableDependencies = project.ProjectReferences
                    .Where(compilations.ContainsKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                references.AddRange(availableDependencies
                    .Select(reference => compilations[reference].ToMetadataReference()));
                if (project.ProjectReferences.Any(reference => !projectByPath.ContainsKey(reference)))
                    warningCodes.Add("csharp.project.reference-missing");
                var compilation = CSharpCompilation.Create(
                    project.AssemblyName,
                    trees,
                    references,
                    CreateCompilationOptions(project));
                compilations[project.Path] = compilation;
                var builder = new GraphBuilder(
                    context,
                    plugin,
                    project,
                    availableDependencies);
                builder.AddProject();
                foreach (var tree in trees)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    var source = context.Sources.Single(value => value.Path == tree.FilePath);
                    builder.AddFile(source);
                    builder.AddDeclarations(source.Path, root, model, cancellationToken);
                }
                await builder.WriteAsync(sink, cancellationToken).ConfigureAwait(false);
                contributingSources.UnionWith(builder.ContributingSourcePaths);
                allDiagnostics.AddRange(compilation.GetDiagnostics(cancellationToken));
                warningCodes.UnionWith(project.WarningCodes);
            }

            var diagnostics = allDiagnostics
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .ToArray();
            warningCodes.UnionWith(diagnostics
                .Select(diagnostic => $"csharp.roslyn.{diagnostic.Id.ToLowerInvariant()}")
                .Take(100));
            var obsoleteUnits = context.Changes
                .Where(change =>
                    change.Kind == CodeGraphSourceChangeKind.Deleted &&
                    change.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(change => new CodeIndexUnitId(
                    CSharpProjectDiscovery.IndexUnitId(change.Path)))
                .Append(new CodeIndexUnitId("csharp:repository"))
                .Distinct()
                .ToArray();
            return new CodeGraphExtractionResult(
                obsoleteUnits,
                sourcesExamined: context.Sources.Count,
                sourcesContributingFacts: contributingSources.Count,
                unresolvedRelationships: diagnostics.Count(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error &&
                    UnresolvedDiagnosticIds.Contains(diagnostic.Id)),
                warningCodes: warningCodes.Order(StringComparer.Ordinal).Take(100).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GraphBuilder(
        CodeGraphPluginContext context,
        CSharpCodeGraphPlugin plugin,
        CSharpProjectModel project,
        IReadOnlySet<string> availableDependencies)
    {
        private readonly SortedDictionary<string, CodeGraphNode> _nodes =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphDeclaration> _declarations =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphEdge> _edges =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _contributingSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodeNodeId> _fileNodes = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> ContributingSourcePaths => _contributingSources;

        public void AddProject()
        {
            var id = ProjectNodeId(project.Path);
            _nodes.Add(
                id.Value,
                new CodeGraphNode(
                    id,
                    CodeNodeKinds.Project,
                    project.Name,
                    project.Path,
                    properties: new Dictionary<string, CodePropertyValue>
                    {
                        ["language"] = new CodeTextProperty("csharp"),
                        ["assembly-name"] = new CodeTextProperty(project.AssemblyName),
                        ["target-framework"] = new CodeTextProperty(project.TargetFramework ?? string.Empty),
                        ["nullable"] = new CodeTextProperty(project.Nullable ?? string.Empty),
                        ["implicit-usings"] = new CodeBooleanProperty(project.ImplicitUsings),
                        ["define-constants"] = new CodeTextListProperty(project.DefineConstants)
                    }));
            foreach (var reference in project.ProjectReferences.Where(
                         availableDependencies.Contains))
            {
                AddEdge(
                    CodeEdgeKinds.DependsOn,
                    id,
                    ProjectNodeId(reference),
                    ProjectLocation());
            }
        }

        public void AddFile(CodeGraphSource source)
        {
            var id = NodeId("file", source.Path);
            _fileNodes.Add(source.Path, id);
            _nodes.Add(
                id.Value,
                new CodeGraphNode(
                    id,
                    CodeNodeKinds.File,
                    System.IO.Path.GetFileName(source.Path),
                    source.Path,
                    properties: new Dictionary<string, CodePropertyValue>
                    {
                        ["language"] = new CodeTextProperty("csharp"),
                        ["content-hash"] = new CodeTextProperty(source.ContentHash)
                    }));
            AddEdge(
                CodeEdgeKinds.Contains,
                ProjectNodeId(project.Path),
                id,
                new CodeLocation(source.Path, 1, 1, 1, 1));
        }

        public void AddDeclarations(
            string path,
            SyntaxNode root,
            SemanticModel model,
            CancellationToken cancellationToken)
        {
            foreach (var syntax in root.DescendantNodes(descendIntoTrivia: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbol = GetSupportedSymbol(syntax, model, cancellationToken);
                if (symbol is null)
                    continue;
                var kind = GetNodeKind(symbol);
                if (kind is null)
                    continue;
                AddSymbol(path, syntax, symbol, kind);
                _contributingSources.Add(path);
            }
        }

        public async ValueTask WriteAsync(
            ICodeGraphSink sink,
            CancellationToken cancellationToken)
        {
            var origin = new CodeFactOrigin(
                context.RepositoryId,
                plugin.Id,
                plugin.Version,
                context.IndexRunId,
                new CodeIndexUnitId(CSharpProjectDiscovery.IndexUnitId(project.Path)));
            await WriteChunksAsync(
                _nodes.Values,
                sink.Limits.MaxNodes,
                values => new CodeGraphBatch(origin, nodes: values),
                sink,
                cancellationToken).ConfigureAwait(false);
            await WriteChunksAsync(
                _declarations.Values,
                sink.Limits.MaxDeclarations,
                values => new CodeGraphBatch(origin, declarations: values),
                sink,
                cancellationToken).ConfigureAwait(false);
            await WriteChunksAsync(
                _edges.Values,
                sink.Limits.MaxEdges,
                values => new CodeGraphBatch(origin, edges: values),
                sink,
                cancellationToken).ConfigureAwait(false);
            await sink.WriteBatchAsync(
                new CodeGraphBatch(origin, completesIndexUnit: true),
                cancellationToken).ConfigureAwait(false);
        }

        private void AddSymbol(
            string path,
            SyntaxNode syntax,
            ISymbol symbol,
            CodeNodeKind kind)
        {
            var canonical = ScopedSymbolKey(symbol);
            var symbolId = new CodeSymbolId($"csharp:{Hash(canonical)}");
            var nodeId = NodeId("symbol", canonical);
            var qualifiedName = QualifiedName(symbol);
            var node = new CodeGraphNode(
                nodeId,
                kind,
                DisplayName(symbol),
                qualifiedName,
                symbolId,
                new Dictionary<string, CodePropertyValue>
                {
                    ["language"] = new CodeTextProperty("csharp"),
                    ["canonical-key"] = new CodeTextProperty(canonical),
                    ["symbol-kind"] = new CodeTextProperty(symbol.Kind.ToString().ToLowerInvariant())
                });
            if (_nodes.TryGetValue(nodeId.Value, out var existing) && !NodesEquivalent(existing, node))
            {
                throw new InvalidOperationException(
                    $"Roslyn produced inconsistent facts for symbol '{canonical}'.");
            }
            _nodes[nodeId.Value] = node;

            var location = Location(path, syntax);
            var declarationId = new CodeDeclarationId(
                $"csharp:{Hash($"{canonical}\n{path}\n{location.StartLine}:{location.StartColumn}:{location.EndLine}:{location.EndColumn}")}");
            _declarations[declarationId.Value] = new(
                declarationId,
                symbolId,
                nodeId,
                location);
            AddEdge(
                CodeEdgeKinds.Declares,
                _fileNodes[path],
                nodeId,
                location,
                declarationId.Value);

            var containing = symbol.ContainingSymbol;
            if (containing is not null &&
                containing is not IAssemblySymbol and not IModuleSymbol &&
                containing is not INamespaceSymbol { IsGlobalNamespace: true })
            {
                var containingId = NodeId("symbol", ScopedSymbolKey(containing));
                if (_nodes.ContainsKey(containingId.Value))
                    AddEdge(CodeEdgeKinds.Contains, containingId, nodeId, location);
            }
        }

        private string ScopedSymbolKey(ISymbol symbol) =>
            $"{project.Path}\n{CanonicalSymbolKey(symbol)}";

        private CodeLocation ProjectLocation() => new(
            project.Path == "@loose/csharp" ? "@loose/csharp" : project.Path,
            1,
            1,
            1,
            1);

        private static bool NodesEquivalent(CodeGraphNode first, CodeGraphNode second) =>
            first.Id == second.Id &&
            first.Kind == second.Kind &&
            first.Name == second.Name &&
            first.QualifiedName == second.QualifiedName &&
            first.SymbolId == second.SymbolId &&
            first.Properties.Count == second.Properties.Count &&
            first.Properties.All(pair =>
                second.Properties.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private void AddEdge(
            CodeEdgeKind kind,
            CodeNodeId source,
            CodeNodeId target,
            CodeLocation location,
            string? discriminator = null)
        {
            var id = new CodeEdgeId(
                $"csharp:{Hash($"{kind.Value}\n{source.Value}\n{target.Value}\n{discriminator}")}");
            _edges[id.Value] = new(
                id,
                source,
                target,
                kind,
                new CodeEvidence(
                    CodeEvidenceKind.Semantic,
                    plugin.Id.Value,
                    plugin.Version,
                    location));
        }

        private static async ValueTask WriteChunksAsync<T>(
            IEnumerable<T> values,
            int chunkSize,
            Func<IReadOnlyList<T>, CodeGraphBatch> createBatch,
            ICodeGraphSink sink,
            CancellationToken cancellationToken)
        {
            foreach (var chunk in values.Chunk(chunkSize))
            {
                await sink.WriteBatchAsync(createBatch(chunk), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static ISymbol? GetSupportedSymbol(
        SyntaxNode syntax,
        SemanticModel model,
        CancellationToken cancellationToken) => syntax switch
        {
            BaseNamespaceDeclarationSyntax value => model.GetDeclaredSymbol(value, cancellationToken),
            BaseTypeDeclarationSyntax value => model.GetDeclaredSymbol(value, cancellationToken),
            DelegateDeclarationSyntax value => model.GetDeclaredSymbol(value, cancellationToken),
            BaseMethodDeclarationSyntax value => model.GetDeclaredSymbol(value, cancellationToken),
            BasePropertyDeclarationSyntax value => model.GetDeclaredSymbol(value, cancellationToken),
            VariableDeclaratorSyntax value when value.Parent?.Parent is FieldDeclarationSyntax =>
                model.GetDeclaredSymbol(value, cancellationToken),
            ParameterSyntax value when HasSupportedParameterOwner(value) =>
                model.GetDeclaredSymbol(value, cancellationToken),
            _ => null
        };

    private static bool HasSupportedParameterOwner(ParameterSyntax parameter) =>
        parameter.Parent?.Parent is BaseMethodDeclarationSyntax or
            DelegateDeclarationSyntax or BasePropertyDeclarationSyntax;

    private static CodeNodeKind? GetNodeKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => CodeNodeKinds.Namespace,
        INamedTypeSymbol named when named.TypeKind == TypeKind.Interface => CodeNodeKinds.Interface,
        INamedTypeSymbol => CodeNodeKinds.Type,
        IMethodSymbol => CodeNodeKinds.Callable,
        IPropertySymbol => CodeNodeKinds.Property,
        IFieldSymbol => CodeNodeKinds.Field,
        IParameterSymbol => CodeNodeKinds.Parameter,
        _ => null
    };

    private static string DisplayName(ISymbol symbol) => symbol is IMethodSymbol
    {
        MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor
    } method
        ? method.ContainingType.Name
        : symbol.Name;

    private static string QualifiedName(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string CanonicalSymbolKey(ISymbol symbol)
    {
        var documentationId = symbol.GetDocumentationCommentId();
        if (documentationId is not null)
            return documentationId;
        if (symbol is IParameterSymbol parameter)
        {
            return $"P:{CanonicalSymbolKey(parameter.ContainingSymbol)}:{parameter.Ordinal}:{parameter.Name}";
        }

        return $"{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
    }

    private static CodeNodeId NodeId(string category, string canonical) =>
        new($"csharp:{category}:{Hash(canonical)}");

    private static CodeNodeId ProjectNodeId(string projectPath) =>
        NodeId("project", projectPath);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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

    private static IReadOnlyList<CSharpProjectModel> OrderProjects(
        IReadOnlyList<CSharpProjectModel> projects,
        IReadOnlyDictionary<string, CSharpProjectModel> projectByPath,
        ISet<string> warningCodes)
    {
        var ordered = new List<CSharpProjectModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(CSharpProjectModel project)
        {
            if (visited.Contains(project.Path))
                return;
            if (!visiting.Add(project.Path))
            {
                warningCodes.Add("csharp.project.reference-cycle");
                return;
            }
            foreach (var reference in project.ProjectReferences)
            {
                if (projectByPath.TryGetValue(reference, out var dependency))
                    Visit(dependency);
            }
            visiting.Remove(project.Path);
            visited.Add(project.Path);
            ordered.Add(project);
        }

        foreach (var project in projects.OrderBy(project => project.Path, StringComparer.Ordinal))
            Visit(project);
        return ordered;
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

    private static IReadOnlyList<MetadataReference> CreatePlatformReferences()
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
