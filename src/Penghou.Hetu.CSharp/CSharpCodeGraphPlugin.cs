using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
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
            var runSymbols = new RunSymbols();
            var relationshipTotals = new Dictionary<string, RelationshipCounters>(
                StringComparer.Ordinal);
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
                    availableDependencies,
                    runSymbols);
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
                builder.AddRelationships(cancellationToken);
                await builder.WriteAsync(sink, cancellationToken).ConfigureAwait(false);
                contributingSources.UnionWith(builder.ContributingSourcePaths);
                allDiagnostics.AddRange(compilation.GetDiagnostics(cancellationToken));
                warningCodes.UnionWith(project.WarningCodes);
                foreach (var (kind, counters) in builder.Counters)
                {
                    var total = relationshipTotals.TryGetValue(
                        kind,
                        out var existing)
                        ? existing
                        : new RelationshipCounters();
                    total.EdgesEmitted += counters.EdgesEmitted;
                    total.UnresolvedTargets += counters.UnresolvedTargets;
                    relationshipTotals[kind] = total;
                }
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
                .Distinct()
                .ToArray();
            return new CodeGraphExtractionResult(
                obsoleteUnits,
                sourcesExamined: context.Sources.Count,
                sourcesContributingFacts: contributingSources.Count,
                unresolvedRelationships: diagnostics.Count(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error &&
                    UnresolvedDiagnosticIds.Contains(diagnostic.Id)),
                warningCodes: warningCodes.Order(StringComparer.Ordinal).Take(100).ToArray(),
                relationshipCoverage: BuildCoverage(relationshipTotals));
        }

        private static IReadOnlyCollection<CodeRelationshipCoverage> BuildCoverage(
            Dictionary<string, RelationshipCounters> totals)
        {
            var coverage = new List<CodeRelationshipCoverage>();
            foreach (var kind in new[]
                     {
                         CodeEdgeKinds.Inherits,
                         CodeEdgeKinds.Implements,
                         CodeEdgeKinds.Calls,
                         CodeEdgeKinds.References,
                         CodeEdgeKinds.Imports
                     })
            {
                var counters = totals.TryGetValue(
                    kind.Value,
                    out var value)
                    ? value
                    : new RelationshipCounters();
                var state = counters.EdgesEmitted == 0 && counters.UnresolvedTargets == 0
                    ? CodeRelationshipCoverageState.NotProduced
                    : counters.UnresolvedTargets > 0
                        ? CodeRelationshipCoverageState.Partial
                        : CodeRelationshipCoverageState.Produced;
                coverage.Add(new(
                    kind.Value,
                    state,
                    counters.EdgesEmitted,
                    counters.UnresolvedTargets));
            }

            // Return/parameter typing is deliberately not produced yet: the
            // cross-language meaning is not precise enough to publish without
            // guessing. The not-produced entries keep that decision visible.
            coverage.Add(new(
                CodeEdgeKinds.Returns.Value,
                CodeRelationshipCoverageState.NotProduced,
                0,
                0));
            coverage.Add(new(
                CodeEdgeKinds.Accepts.Value,
                CodeRelationshipCoverageState.NotProduced,
                0,
                0));
            return coverage;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GraphBuilder(
        CodeGraphPluginContext context,
        CSharpCodeGraphPlugin plugin,
        CSharpProjectModel project,
        IReadOnlySet<string> availableDependencies,
        RunSymbols runSymbols)
    {
        private readonly SortedDictionary<string, CodeGraphNode> _nodes =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphDeclaration> _declarations =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, CodeGraphEdge> _edges =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _contributingSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodeNodeId> _fileNodes = new(StringComparer.Ordinal);
        private readonly List<(ISymbol Symbol, CodeNodeId NodeId, CodeLocation Location)> _declared =
            [];
        private readonly List<(string Path, SyntaxNode Root, SemanticModel Model)> _syntax = [];
        private readonly Dictionary<string, RelationshipCounters> _counters =
            new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, RelationshipCounters> Counters => _counters;

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
            _syntax.Add((path, root, model));
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

        /// <summary>
        /// Second pass over the completed declaration set: resolves semantic
        /// relationships now that every node of the project exists. Targets
        /// resolve against this project first, then any project processed
        /// earlier in dependency order; targets outside the indexed repository
        /// are skipped silently, and genuinely unresolved targets are counted
        /// per relationship kind instead of producing guessed edges.
        /// </summary>
        public void AddRelationships(CancellationToken cancellationToken)
        {
            foreach (var (symbol, nodeId, location) in _declared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (symbol is not INamedTypeSymbol namedType)
                    continue;

                // Only skip the implicit object base; SpecialType.None means
                // a normal user-defined base class.
                if (namedType.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                {
                    TryAddRelationshipEdge(
                        CodeEdgeKinds.Inherits,
                        nodeId,
                        Normalize(baseType),
                        location);
                }

                foreach (var interfaceType in namedType.Interfaces)
                {
                    TryAddRelationshipEdge(
                        CodeEdgeKinds.Implements,
                        nodeId,
                        Normalize(interfaceType),
                        location);
                }
            }

            foreach (var (path, root, model) in _syntax)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var syntax in root.DescendantNodes(descendIntoTrivia: false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (syntax)
                    {
                        case InvocationExpressionSyntax invocation:
                            HandleInvocation(path, invocation, model);
                            break;
                        case ObjectCreationExpressionSyntax creation:
                            HandleCreation(path, creation, model);
                            break;
                        case TypeOfExpressionSyntax typeOf:
                            HandleTypeUsage(path, typeOf.Type, model, typeOf.GetLocation());
                            break;
                        case IsPatternExpressionSyntax
                        {
                            Pattern: TypePatternSyntax typePattern
                        }:
                            HandleTypeUsage(path, typePattern.Type, model, typePattern.GetLocation());
                            break;
                        case BinaryExpressionSyntax binary when
                            binary.IsKind(SyntaxKind.IsExpression) ||
                            binary.IsKind(SyntaxKind.AsExpression):
                            if (binary.Right is TypeSyntax rightType)
                                HandleTypeUsage(path, rightType, model, binary.GetLocation());
                            break;
                        case UsingDirectiveSyntax usingDirective:
                            HandleUsingDirective(path, usingDirective, model);
                            break;
                    }
                }
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
            var location = Location(path, syntax);

            // Register in the run-wide map before constructing properties so
            // later projects can resolve this symbol as a relationship target.
            // The canonical key is already globally unique (doc-comment ID or
            // fully qualified display string); no project prefix needed.
            runSymbols.Register(CanonicalSymbolKey(symbol), nodeId);

            var node = new CodeGraphNode(
                nodeId,
                kind,
                DisplayName(symbol),
                qualifiedName,
                symbolId,
                BuildProperties(symbol, syntax));
            if (_nodes.TryGetValue(nodeId.Value, out var existing) && !NodesEquivalent(existing, node))
            {
                var differingKeys = string.Join(", ", existing.Properties.Keys
                    .Union(node.Properties.Keys)
                    .Where(key => !Equals(
                        existing.Properties.TryGetValue(key, out var firstValue) ? firstValue : null,
                        node.Properties.TryGetValue(key, out var secondValue) ? secondValue : null)));
                throw new InvalidOperationException(
                    $"Roslyn produced inconsistent facts for symbol '{canonical}'; " +
                    $"differing: [{differingKeys}]; " +
                    $"existing modifiers={(existing.Properties.ContainsKey("modifiers") ? string.Join("/", ((CodeTextListProperty)existing.Properties["modifiers"]).Values) : "none")}; " +
                    $"new modifiers={(node.Properties.ContainsKey("modifiers") ? string.Join("/", ((CodeTextListProperty)node.Properties["modifiers"]).Values) : "none")}.");
            }
            _nodes[nodeId.Value] = node;

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

            _declared.Add((Normalize(symbol), nodeId, location));
        }

        private Dictionary<string, CodePropertyValue> BuildProperties(ISymbol symbol, SyntaxNode syntax)
        {
            var properties = new Dictionary<string, CodePropertyValue>
            {
                ["language"] = new CodeTextProperty("csharp"),
                ["canonical-key"] = new CodeTextProperty(CanonicalSymbolKey(symbol)),
                ["symbol-kind"] = new CodeTextProperty(symbol.Kind.ToString().ToLowerInvariant()),
                ["access"] = new CodeTextProperty(
                    symbol.DeclaredAccessibility.ToString().ToLowerInvariant())
            };

            var modifiers = new List<string>();
            AddModifier(modifiers, symbol.IsStatic, "static");
            AddModifier(modifiers, symbol.IsAbstract, "abstract");
            AddModifier(modifiers, symbol.IsVirtual, "virtual");
            AddModifier(modifiers, symbol.IsOverride, "override");
            AddModifier(modifiers, symbol.IsSealed, "sealed");
            if (symbol is IFieldSymbol fieldSymbol)
            {
                AddModifier(modifiers, fieldSymbol.IsReadOnly, "readonly");
                AddModifier(modifiers, fieldSymbol.IsConst, "const");
            }
            if (modifiers.Count > 0)
            {
                properties["modifiers"] = new CodeTextProperty(
                    string.Join(" ", modifiers.OrderBy(value => value, StringComparer.Ordinal)));
            }

            var attributes = symbol.GetAttributes();
            if (attributes.Length > 0)
            {
                var names = attributes
                    .Select(attribute => attribute.AttributeClass?.Name)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(16)
                    .ToArray();
                properties["attributes"] = new CodeTextProperty(string.Join(" ", names));
                if (names.Contains("ObsoleteAttribute"))
                    properties["obsolete"] = new CodeBooleanProperty(true);
            }

            if (symbol is IFieldSymbol { HasConstantValue: true } constant &&
                constant.ConstantValue is not null)
            {
                var literal = ToLiteral(constant.ConstantValue);
                if (literal is not null)
                    properties["constant-value"] = literal;
            }

            var summary = GetDocSummary(syntax);
            if (summary is not null)
                properties["doc-summary"] = new CodeTextProperty(summary);

            return properties;
        }

        private static string? GetDocSummary(SyntaxNode syntax)
        {
            var firstToken = syntax.GetFirstToken(includeZeroWidth: true);
            var allTrivia = new List<SyntaxTrivia>();
            var current = firstToken;
            for (var i = 0; i < 10; i++)
            {
                allTrivia.AddRange(current.LeadingTrivia);
                var previous = current.GetPreviousToken();
                if (previous == default)
                    break;
                allTrivia.AddRange(previous.TrailingTrivia);
                current = previous;
            }

            foreach (var trivia in allTrivia)
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    continue;

                return ExtractSummaryText(trivia.ToFullString());
            }

            return null;
        }

        private static string? ExtractSummaryText(string text)
        {
            var summaryStart = text.IndexOf("<summary>", StringComparison.Ordinal);
            var summaryEnd = text.IndexOf("</summary>", StringComparison.Ordinal);
            if (summaryStart < 0 || summaryEnd <= summaryStart)
                return null;

            var inner = text[(summaryStart + 9)..summaryEnd];
            var collapsed = string.Join(
                ' ',
                inner.Split(
                    [' ', '\r', '\n', '\t', '/'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (collapsed.Length == 0)
                return null;

            return collapsed.Length <= 512
                ? collapsed
                : collapsed[..512];
        }

        private static void AddModifier(List<string> modifiers, bool condition, string name)
        {
            if (condition)
                modifiers.Add(name);
        }

        private static CodePropertyValue? ToLiteral(object value) => value switch
        {
            string text => new CodeTextProperty(text),
            bool flag => new CodeBooleanProperty(flag),
            char character => new CodeTextProperty(character.ToString()),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                new CodeIntegerProperty(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            float or double or decimal =>
                new CodeNumberProperty(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => null
        };

        private static string? GetDocSummary(ISymbol symbol)
        {
            var xml = symbol.GetDocumentationCommentXml(expandIncludes: false);
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            try
            {
                var document = XDocument.Parse($"<root>{xml}</root>");
                var summary = document.Root?
                    .Element("summary")?
                    .Value;
                if (string.IsNullOrWhiteSpace(summary))
                    return null;

                var collapsed = string.Join(
                    ' ',
                    summary.Split(
                        [' ', '\r', '\n', '\t'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return collapsed.Length <= 512
                    ? collapsed
                    : collapsed[..512];
            }
            catch (System.Xml.XmlException)
            {
                return null;
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

        private void Count(string relationshipKind, bool unresolved)
        {
            if (!_counters.TryGetValue(
                    relationshipKind,
                    out var counters))
            {
                counters = new RelationshipCounters();
                _counters[relationshipKind] = counters;
            }

            if (unresolved)
                counters.UnresolvedTargets++;
            else
                counters.EdgesEmitted++;
        }

        private enum TargetMatch
        {
            Found,
            External,
            Ambiguous
        }

        private TargetMatch TryResolveTarget(
            ISymbol target,
            out CodeNodeId nodeId)
        {
            nodeId = null!;

            var scopedId = NodeId("symbol", ScopedSymbolKey(target));
            if (_nodes.ContainsKey(scopedId.Value))
            {
                nodeId = scopedId;
                return TargetMatch.Found;
            }

            var canonical = CanonicalSymbolKey(target);
            if (runSymbols.GlobalNodes.TryGetValue(
                    canonical,
                    out var globalNodeId))
            {
                if (runSymbols.Ambiguous.Contains(canonical))
                    return TargetMatch.Ambiguous;

                nodeId = globalNodeId;
                return TargetMatch.Found;
            }

            return TargetMatch.External;
        }

        private void TryAddRelationshipEdge(
            CodeEdgeKind kind,
            CodeNodeId sourceId,
            ISymbol target,
            CodeLocation location)
        {
            var match = TryResolveTarget(target, out var targetId);
            switch (match)
            {
                case TargetMatch.Found:
                    AddEdge(kind, sourceId, targetId, location);
                    Count(kind.Value, unresolved: false);
                    break;
                case TargetMatch.Ambiguous:
                    // Multiple indexed declarations could be the target; never
                    // guess between them.
                    Count(kind.Value, unresolved: true);
                    break;
                case TargetMatch.External:
                    // Externally-owned symbols (base library, packages) are
                    // neither emitted nor counted as unresolved.
                    break;
            }
        }

        private static ISymbol? EnclosingCallable(SyntaxNode node, SemanticModel model)
        {
            foreach (var ancestor in node.Ancestors())
            {
                switch (ancestor)
                {
                    case BaseMethodDeclarationSyntax method:
                        return model.GetDeclaredSymbol(method);
                    case PropertyDeclarationSyntax property:
                        return model.GetDeclaredSymbol(property);
                    case BaseTypeDeclarationSyntax:
                    case NamespaceDeclarationSyntax:
                        // Callables cannot span outside their declaring type.
                        return null;
                }
            }

            return null;
        }

        private CodeNodeId? SourceNodeFor(ISymbol? callable)
        {
            if (callable is null)
                return null;

            var normalized = Normalize(callable);
            var match = TryResolveTarget(normalized, out var nodeId);
            return match == TargetMatch.Found ? nodeId : null;
        }

        private void HandleInvocation(
            string path,
            InvocationExpressionSyntax invocation,
            SemanticModel model)
        {
            var info = model.GetSymbolInfo(invocation);

            if (info.Symbol is null &&
                info.CandidateSymbols.Length == 0)
            {
                if (SourceNodeFor(EnclosingCallable(invocation, model)) is not null)
                    Count(CodeEdgeKinds.Calls.Value, unresolved: true);
                return;
            }

            if (info.Symbol is not IMethodSymbol method)
                return;

            var targetNode =
                SourceNodeFor(Normalize(method.ReducedFrom ?? method));
            if (targetNode is null)
                return; // external or ambiguous callee

            var sourceNode =
                SourceNodeFor(EnclosingCallable(invocation, model));
            if (sourceNode is null)
                return; // e.g. constructor initializers, field initializers

            AddEdge(
                CodeEdgeKinds.Calls,
                sourceNode,
                targetNode,
                Location(path, invocation));
            Count(CodeEdgeKinds.Calls.Value, unresolved: false);
        }

        private void HandleCreation(
            string path,
            ObjectCreationExpressionSyntax creation,
            SemanticModel model)
        {
            var info = model.GetSymbolInfo(creation);
            if (info.Symbol is IMethodSymbol constructor)
            {
                var constructorTarget =
                    SourceNodeFor(Normalize(constructor));
                var sourceNode =
                    SourceNodeFor(EnclosingCallable(creation, model));
                if (constructorTarget is not null && sourceNode is not null)
                {
                    AddEdge(
                        CodeEdgeKinds.Calls,
                        sourceNode,
                        constructorTarget,
                        Location(path, creation));
                    Count(CodeEdgeKinds.Calls.Value, unresolved: false);
                }
            }
            else
            {
                var createdType = model.GetTypeInfo(creation).Type as INamedTypeSymbol;
                if (createdType is not null)
                    EmitTypeReference(path, createdType, creation.GetLocation(), model, creation);
                else if (info.Symbol is null && info.CandidateSymbols.Length == 0 &&
                         SourceNodeFor(EnclosingCallable(creation, model)) is not null)
                {
                    Count(CodeEdgeKinds.References.Value, unresolved: true);
                }
            }
        }

        private void HandleTypeUsage(
            string path,
            TypeSyntax typeSyntax,
            SemanticModel model,
            Location location)
        {
            var info = model.GetSymbolInfo(typeSyntax);
            if (info.Symbol is INamedTypeSymbol namedType)
            {
                EmitTypeReference(path, namedType, location, model, typeSyntax);
                return;
            }

            if (info.Symbol is null &&
                info.CandidateSymbols.Length == 0 &&
                SourceNodeFor(EnclosingCallable(typeSyntax, model)) is not null)
            {
                Count(CodeEdgeKinds.References.Value, unresolved: true);
            }
        }

        private void EmitTypeReference(
            string path,
            INamedTypeSymbol namedType,
            Location location,
            SemanticModel model,
            SyntaxNode node)
        {
            var sourceNode =
                SourceNodeFor(EnclosingCallable(node, model));
            if (sourceNode is null)
                return;

            TryAddRelationshipEdge(
                CodeEdgeKinds.References,
                sourceNode,
                Normalize(namedType),
                Location(path, node));
        }

        private void HandleUsingDirective(
            string path,
            UsingDirectiveSyntax usingDirective,
            SemanticModel model)
        {
            if (usingDirective.Alias is not null ||
                usingDirective.StaticKeyword != default ||
                usingDirective.Name is null)
            {
                return;
            }

            var info = model.GetSymbolInfo(usingDirective.Name);
            if (info.Symbol is not INamespaceSymbol namespaceSymbol)
                return;

            if (!_fileNodes.TryGetValue(path, out var fileNode))
                return;

            TryAddRelationshipEdge(
                CodeEdgeKinds.Imports,
                fileNode,
                namespaceSymbol,
                new CodeLocation(path, 1, 1, 1, 1));
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

    /// <summary>Symbols shared across every project in one extraction run.</summary>
    private sealed class RunSymbols
    {
        public Dictionary<string, CodeNodeId> GlobalNodes { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> Ambiguous { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Registers a symbol's node. Re-registering the same canonical key
        /// with the same node id (partial types across files) is a no-op.
        /// Only a different node id for the same key is ambiguous.
        /// </summary>
        public void Register(string canonicalKey, CodeNodeId nodeId)
        {
            if (GlobalNodes.TryGetValue(canonicalKey, out var existing))
            {
                if (existing != nodeId)
                    Ambiguous.Add(canonicalKey);
            }
            else
            {
                GlobalNodes[canonicalKey] = nodeId;
            }
        }
    }

    private sealed class RelationshipCounters
    {
        public int EdgesEmitted;
        public int UnresolvedTargets;
    }

    private static ISymbol Normalize(ISymbol symbol) =>
        symbol is IMethodSymbol method
            ? method.ReducedFrom ?? method.OriginalDefinition
            : symbol.OriginalDefinition;

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
