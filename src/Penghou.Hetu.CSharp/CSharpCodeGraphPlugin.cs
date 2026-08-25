using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Penghou.Hetu;

/// <summary>Deterministic repository-aware C# extraction powered by Roslyn.</summary>
public sealed class CSharpCodeGraphPlugin : ICodeGraphPlugin
{
    public CodePluginId Id { get; } = new("penghou.hetu.csharp");
    public string Version => "0.1.0-preview.1";
    public string Language => "csharp";
    public IReadOnlyCollection<string> FileExtensions => [".cs"];
    public CodeGraphCapabilities Capabilities =>
        CodeGraphCapabilities.Syntax |
        CodeGraphCapabilities.Symbols |
        CodeGraphCapabilities.Types;

    public bool CanHandle(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

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
            var trees = new List<(CodeGraphSource Source, SyntaxTree Tree)>();
            foreach (var source in context.Sources.OrderBy(source => source.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var tree = CSharpSyntaxTree.ParseText(
                    text,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    source.Path,
                    cancellationToken: cancellationToken);
                trees.Add((source, tree));
            }

            var compilation = CSharpCompilation.Create(
                "Penghou.Hetu.CSharp.Index",
                trees.Select(item => item.Tree),
                CreatePlatformReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    deterministic: true,
                    concurrentBuild: false));
            var builder = new GraphBuilder(context, plugin);
            foreach (var (source, tree) in trees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                builder.AddFile(source);
                builder.AddDeclarations(source.Path, root, model, cancellationToken);
            }

            await builder.WriteAsync(sink, cancellationToken).ConfigureAwait(false);
            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .ToArray();
            var warningCodes = diagnostics
                .Select(diagnostic => $"csharp.roslyn.{diagnostic.Id.ToLowerInvariant()}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(100)
                .ToArray();
            return new CodeGraphExtractionResult(
                sourcesExamined: context.Sources.Count,
                sourcesContributingFacts: builder.ContributingSources,
                unresolvedRelationships: diagnostics.Count(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error),
                warningCodes: warningCodes);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GraphBuilder(
        CodeGraphPluginContext context,
        CSharpCodeGraphPlugin plugin)
    {
        private readonly SortedDictionary<string, CodeGraphNode> _nodes =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphDeclaration> _declarations =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphEdge> _edges =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _contributingSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodeNodeId> _fileNodes = new(StringComparer.Ordinal);

        public int ContributingSources => _contributingSources.Count;

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
                new CodeIndexUnitId("csharp:repository"));
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
            var canonical = CanonicalSymbolKey(symbol);
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
                var containingId = NodeId("symbol", CanonicalSymbolKey(containing));
                if (_nodes.ContainsKey(containingId.Value))
                    AddEdge(CodeEdgeKinds.Contains, containingId, nodeId, location);
            }
        }

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
}
