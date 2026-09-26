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
            var sourcesByPath = context.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
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

            var discovery = CSharpProjectDiscovery.Discover(content);
            var projects = discovery.Projects;
            var projectByPath = projects.ToDictionary(
                project => project.Path,
                CSharpProjectDiscovery.PathComparer);
            var compilations = new Dictionary<string, CSharpCompilation>(
                CSharpProjectDiscovery.PathComparer);
            var allDiagnostics = new List<Diagnostic>();
            var warningCodes = new HashSet<string>(StringComparer.Ordinal);
            warningCodes.UnionWith(discovery.Warnings);
            var contributingSources = new HashSet<string>(StringComparer.Ordinal);
            var runSymbols = new RunSymbols();
            var relationshipTotals = new Dictionary<string, RelationshipCounters>(
                StringComparer.Ordinal);
            var indexUnitCoverage = new List<CodeIndexUnitCoverage>();
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
                    .ToHashSet(CSharpProjectDiscovery.PathComparer);
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
                    var source = sourcesByPath[tree.FilePath];
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
                    total.Candidates += counters.Candidates;
                    total.InternalTargets += counters.InternalTargets;
                    total.CrossProjectTargets += counters.CrossProjectTargets;
                    total.ExternalTargets += counters.ExternalTargets;
                    total.AmbiguousTargets += counters.AmbiguousTargets;
                    total.UnsupportedTargets += counters.UnsupportedTargets;
                    relationshipTotals[kind] = total;
                }

                indexUnitCoverage.Add(new CodeIndexUnitCoverage(
                    new CodeIndexUnitId(CSharpProjectDiscovery.IndexUnitId(project.Path)),
                    BuildCoverage(
                        builder.Counters,
                        hasSources: trees.Length > 0)));
            }

            var diagnostics = allDiagnostics
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .ToArray();
            warningCodes.UnionWith(diagnostics
                .Select(diagnostic => $"csharp.roslyn.{diagnostic.Id.ToLowerInvariant()}")
                .Take(100));
            var currentUnits = projects
                .Select(project => new CodeIndexUnitId(CSharpProjectDiscovery.IndexUnitId(project.Path)))
                .ToHashSet();
            var obsoleteUnits = context.Changes
                .Where(change =>
                    change.Kind == CodeGraphSourceChangeKind.Deleted &&
                    change.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(change => new CodeIndexUnitId(
                    CSharpProjectDiscovery.IndexUnitId(change.Path)))
                .Concat(context.PreviousIndexUnits.Where(unit => !currentUnits.Contains(unit)))
                .Distinct()
                .OrderBy(unit => unit.Value, StringComparer.Ordinal)
                .ToArray();
            return new CodeGraphExtractionResult(
                obsoleteUnits,
                sourcesExamined: context.Sources.Count,
                sourcesContributingFacts: contributingSources.Count,
                unresolvedRelationships: diagnostics.Count(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error &&
                    UnresolvedDiagnosticIds.Contains(diagnostic.Id)),
                warningCodes: warningCodes.Order(StringComparer.Ordinal).Take(100).ToArray(),
                relationshipCoverage: BuildCoverage(relationshipTotals, hasSources: projects.Count > 0),
                indexUnitCoverage: indexUnitCoverage);
        }

        private static IReadOnlyCollection<CodeRelationshipCoverage> BuildCoverage(
            IReadOnlyDictionary<string, RelationshipCounters> totals,
            bool hasSources)
        {
            var coverage = new List<CodeRelationshipCoverage>();
            foreach (var kind in new[]
                     {
                         CodeEdgeKinds.Inherits,
                         CodeEdgeKinds.Implements,
                         CodeEdgeKinds.Calls,
                         CodeEdgeKinds.References,
                         CodeEdgeKinds.Imports,
                         CodeEdgeKinds.ExercisedBy
                     })
            {
                var counters = totals.TryGetValue(
                    kind.Value,
                    out var value)
                    ? value
                    : new RelationshipCounters();
                coverage.Add(ToCoverage(kind.Value, counters, hasSources));
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

        private static CodeRelationshipCoverage ToCoverage(
            string kind,
            RelationshipCounters counters,
            bool hasSources)
        {
            // A scan that ran and found nothing is a complete empty result,
            // not a missing one. Only a unit without sources cannot claim
            // anything about its relationships.
            if (counters.Candidates == 0)
            {
                var empty = hasSources
                    ? CodeRelationshipCoverageState.Produced
                    : CodeRelationshipCoverageState.Unavailable;
                return new(kind, empty, 0, 0);
            }

            var state = counters.UnresolvedTargets > 0
                ? CodeRelationshipCoverageState.Partial
                : CodeRelationshipCoverageState.Produced;
            return new(
                kind,
                state,
                counters.EdgesEmitted,
                counters.UnresolvedTargets,
                counters.Candidates,
                counters.InternalTargets,
                counters.CrossProjectTargets,
                counters.ExternalTargets,
                counters.AmbiguousTargets,
                counters.UnsupportedTargets);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public int Candidates;
        public int InternalTargets;
        public int CrossProjectTargets;
        public int ExternalTargets;
        public int AmbiguousTargets;
        public int UnsupportedTargets;
    }

    private static IReadOnlyList<CSharpProjectModel> OrderProjects(
        IReadOnlyList<CSharpProjectModel> projects,
        IReadOnlyDictionary<string, CSharpProjectModel> projectByPath,
        ISet<string> warningCodes)
    {
        var ordered = new List<CSharpProjectModel>();
        var visited = new HashSet<string>(CSharpProjectDiscovery.PathComparer);
        var visiting = new HashSet<string>(CSharpProjectDiscovery.PathComparer);

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
}
