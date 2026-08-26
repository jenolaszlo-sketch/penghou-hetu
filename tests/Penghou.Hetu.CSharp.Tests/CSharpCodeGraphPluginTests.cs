using System.Text;
using System.Text.Json;
using System.Reflection;

namespace Penghou.Hetu.CSharp.Tests;

public sealed class CSharpCodeGraphPluginTests
{
    [Fact]
    public async Task ExtractAsync_ModelsPartialTypesMembersParametersAndContainment()
    {
        var extracted = await ExtractAsync(
            ("src/Widget.One.cs", """
                namespace Example;

                public partial class Widget
                {
                    private readonly int _value;
                    public Widget(int value) => _value = value;
                    public void Run(int count) { }
                }
                """),
            ("src/Widget.Two.cs", """
                namespace Example;

                public partial class Widget
                {
                    public string Name { get; } = "widget";
                    public void Run(string text) { }
                }
                """));

        var widget = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Example.Widget");
        Assert.Equal(
            2,
            extracted.Declarations.Count(declaration => declaration.SymbolId == widget.SymbolId));
        Assert.Equal(
            2,
            extracted.Nodes.Count(node =>
                node.Kind == CodeNodeKinds.Callable &&
                node.QualifiedName?.StartsWith("Example.Widget.Run(", StringComparison.Ordinal) == true));
        Assert.Contains(extracted.Nodes, node => node.Kind == CodeNodeKinds.Property && node.Name == "Name");
        Assert.Contains(extracted.Nodes, node => node.Kind == CodeNodeKinds.Field && node.Name == "_value");
        Assert.Contains(extracted.Nodes, node => node.Kind == CodeNodeKinds.Parameter && node.Name == "value");
        Assert.Contains(extracted.Nodes, node => node.Kind == CodeNodeKinds.Parameter && node.Name == "count");
        Assert.Contains(extracted.Nodes, node => node.Kind == CodeNodeKinds.Parameter && node.Name == "text");
        Assert.Equal(2, extracted.Nodes.Count(node => node.Kind == CodeNodeKinds.File));
        Assert.All(
            extracted.Edges,
            edge =>
            {
                Assert.Contains(extracted.Nodes, node => node.Id == edge.SourceId);
                Assert.Contains(extracted.Nodes, node => node.Id == edge.TargetId);
            });
        Assert.Contains(
            extracted.Edges,
            edge => edge.Kind == CodeEdgeKinds.Contains &&
                edge.SourceId == widget.Id &&
                extracted.Nodes.Single(node => node.Id == edge.TargetId).Name == "Name");
        Assert.Equal(2, extracted.Result.SourcesExamined);
        Assert.Equal(2, extracted.Result.SourcesContributingFacts);
        Assert.Equal(0, extracted.Result.UnresolvedRelationships);
    }

    [Fact]
    public async Task ExtractAsync_RepeatsNormalizedFactsDeterministically()
    {
        var sources = new[]
        {
            ("src/B.cs", "namespace Example; public class B { public void M(int value) { } }"),
            ("src/A.cs", "namespace Example; public interface A { void Execute(); }")
        };

        var first = await ExtractAsync(sources);
        var second = await ExtractAsync(sources.Reverse().ToArray());

        Assert.Equal(JsonSerializer.Serialize(first.Nodes), JsonSerializer.Serialize(second.Nodes));
        Assert.Equal(
            JsonSerializer.Serialize(first.Declarations),
            JsonSerializer.Serialize(second.Declarations));
        Assert.Equal(JsonSerializer.Serialize(first.Edges), JsonSerializer.Serialize(second.Edges));
    }

    [Fact]
    public async Task ExtractAsync_ReportsCompilerProblemsWithoutSourceContent()
    {
        var extracted = await ExtractAsync(
            ("src/Broken.cs", "namespace Example; public class Broken : MissingBase { }"));

        Assert.True(extracted.Result.UnresolvedRelationships > 0);
        Assert.Contains(
            extracted.Result.WarningCodes,
            code => code.StartsWith("csharp.roslyn.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            extracted.Result.WarningCodes,
            code => code.Contains("MissingBase", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicApi_DoesNotExposeRoslynTypes()
    {
        var exported = typeof(CSharpCodeGraphPlugin).Assembly.GetExportedTypes();

        Assert.Equal([typeof(CSharpCodeGraphPlugin)], exported);
        Assert.DoesNotContain(
            exported.SelectMany(type => type.GetMembers()),
            member => member.ToString()?.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Version_MatchesPackageInformationalVersionWithoutBuildMetadata()
    {
        var informational = typeof(CSharpCodeGraphPlugin).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion
            .Split('+', 2)[0];

        Assert.Equal(informational, new CSharpCodeGraphPlugin().Version);
    }

    [Fact]
    public async Task ExtractAsync_DoesNotMislabelSyntaxErrorsAsUnresolvedRelationships()
    {
        var extracted = await ExtractAsync(
            ("src/SyntaxError.cs", "namespace Example; public class Broken { public void M( { }"));

        Assert.Equal(0, extracted.Result.UnresolvedRelationships);
        Assert.NotEmpty(extracted.Result.WarningCodes);
    }

    [Fact]
    public async Task IndexingLifecycle_PersistsAndSkipsUnchangedCSharpRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hetu-csharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "One.cs"),
                "namespace Example; public partial class Widget { public void One() { } }");
            await File.WriteAllTextAsync(
                Path.Combine(root, "Two.cs"),
                "namespace Example; public partial class Widget { public void Two() { } }");
            var repositoryId = new CodeRepositoryId("repo:csharp-integration");
            var plugin = new CSharpCodeGraphPlugin();
            var store = new InMemoryCodeGraphStore();
            var indexing = new CodeIndexingService(
                new CodeRepositoryProviderRegistry([new FileSystemCodeRepositoryProvider()]),
                new CodeGraphPluginRegistry([plugin]),
                store);
            var descriptor = new CodeRepositoryDescriptor(repositoryId, root);

            var first = await indexing.IndexAsync(descriptor, new("run:first"));
            var second = await indexing.IndexAsync(descriptor, new("run:second"));

            var widget = Assert.Single(
                await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Widget"));
            Assert.Equal(
                2,
                (await store.GetDeclarationsAsync(repositoryId, widget.SymbolId!)).Count);
            Assert.Equal(1, first.Diagnostics.PluginsExecuted);
            Assert.Equal(0, second.Diagnostics.PluginsExecuted);
            Assert.Equal(2, second.Diagnostics.FilesUnchanged);

            await File.WriteAllTextAsync(
                Path.Combine(root, "One.cs"),
                "namespace Example; public partial class Widget { public void One() { } public void Three() { } }");
            var third = await indexing.IndexAsync(descriptor, new("run:partial-change"));
            widget = Assert.Single(
                await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Widget"));
            Assert.Equal(
                2,
                (await store.GetDeclarationsAsync(repositoryId, widget.SymbolId!)).Count);
            Assert.Single(
                await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Widget.Three()"));
            Assert.Equal(1, third.Diagnostics.FilesChanged);
            Assert.Equal(1, third.Diagnostics.FilesUnchanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IndexingLifecycle_ProjectOptionChangesAndDeletionReplaceCorrectUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hetu-csharp-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "Example.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Conditional.cs"),
                "namespace Example; public class Always { }\n#if FEATURE\npublic class Enabled { }\n#endif");
            var repositoryId = new CodeRepositoryId("repo:csharp-project-lifecycle");
            var plugin = new CSharpCodeGraphPlugin();
            var store = new InMemoryCodeGraphStore();
            var indexing = new CodeIndexingService(
                new CodeRepositoryProviderRegistry([new FileSystemCodeRepositoryProvider()]),
                new CodeGraphPluginRegistry([plugin]),
                store);
            var descriptor = new CodeRepositoryDescriptor(repositoryId, root);

            await indexing.IndexAsync(descriptor, new("run:without-feature"));
            Assert.Empty(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Enabled"));

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>FEATURE</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            await indexing.IndexAsync(descriptor, new("run:with-feature"));
            Assert.Single(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Enabled"));

            File.Delete(projectPath);
            await indexing.IndexAsync(descriptor, new("run:deleted-project"));
            Assert.Empty(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.csproj"));
            Assert.Single(await store.FindNodesByQualifiedNameAsync(repositoryId, "@loose/csharp"));
            Assert.Empty(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Enabled"));
            Assert.Single(await store.FindNodesByQualifiedNameAsync(repositoryId, "Example.Always"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IndexingLifecycle_ChangedProjectReferenceRemovesDependencyEdge()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hetu-csharp-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Lib", "Lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await File.WriteAllTextAsync(
                Path.Combine(root, "Lib", "Value.cs"),
                "namespace Lib; public class Value { }");
            var appProjectPath = Path.Combine(root, "App", "App.csproj");
            await File.WriteAllTextAsync(appProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root, "App", "Application.cs"),
                "namespace App; public class Application { }");
            var repositoryId = new CodeRepositoryId("repo:csharp-reference-change");
            var store = new InMemoryCodeGraphStore();
            var indexing = new CodeIndexingService(
                new CodeRepositoryProviderRegistry([new FileSystemCodeRepositoryProvider()]),
                new CodeGraphPluginRegistry([new CSharpCodeGraphPlugin()]),
                store);
            var descriptor = new CodeRepositoryDescriptor(repositoryId, root);

            await indexing.IndexAsync(descriptor, new("run:with-reference"));
            var app = Assert.Single(await store.FindNodesByQualifiedNameAsync(repositoryId, "App/App.csproj"));
            var withReference = await store.TraverseAsync(
                repositoryId,
                new(app.Id, edgeKinds: [CodeEdgeKinds.DependsOn]));
            Assert.Single(withReference.Edges);

            await File.WriteAllTextAsync(appProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await indexing.IndexAsync(descriptor, new("run:without-reference"));
            app = Assert.Single(await store.FindNodesByQualifiedNameAsync(repositoryId, "App/App.csproj"));
            var withoutReference = await store.TraverseAsync(
                repositoryId,
                new(app.Id, edgeKinds: [CodeEdgeKinds.DependsOn]));
            Assert.Empty(withoutReference.Edges);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_ModelsProjectsReferencesLinkedFilesAndCompileRemovals()
    {
        var extracted = await ExtractAsync(
            ("Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <AssemblyName>Example.Library</AssemblyName>
                  </PropertyGroup>
                </Project>
                """),
            ("Lib/Value.cs", "namespace Lib; public class Value { }"),
            ("App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>FEATURE_A;FEATURE_B</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Lib/Lib.csproj" />
                    <Compile Include="../Shared/Linked.cs" />
                    <Compile Remove="Excluded.cs" />
                  </ItemGroup>
                </Project>
                """),
            ("App/App.cs", "namespace App; public class Application { private Lib.Value? _value; }"),
            ("App/Excluded.cs", "namespace App; public class Excluded { }"),
            ("Shared/Linked.cs", "namespace App; public class Linked { }"));

        var appProject = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Project && node.QualifiedName == "App/App.csproj");
        var libProject = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Project && node.QualifiedName == "Lib/Lib.csproj");
        Assert.Contains(
            extracted.Edges,
            edge => edge.Kind == CodeEdgeKinds.DependsOn &&
                edge.SourceId == appProject.Id && edge.TargetId == libProject.Id);
        Assert.Contains(
            extracted.Edges,
            edge => edge.Kind == CodeEdgeKinds.Contains &&
                edge.SourceId == appProject.Id &&
                extracted.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName == "Shared/Linked.cs");
        Assert.DoesNotContain(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.File && node.QualifiedName == "App/Excluded.cs");
        Assert.Contains(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Lib.Value");
        Assert.Equal(2, extracted.UnitIds.Count);
        Assert.Equal(0, extracted.Result.UnresolvedRelationships);
        Assert.DoesNotContain(
            extracted.Result.ObsoleteIndexUnits,
            unit => unit.Value == "csharp:repository");
    }

    [Fact]
    public async Task ExtractAsync_ReportsDeletedProjectUnitForAtomicCleanup()
    {
        var context = new CodeGraphPluginContext(
            new CodeRepositoryId("repo:test"),
            "memory://test",
            new CodeIndexRunId("run:test"),
            [],
            changes:
            [
                new CodeGraphSourceChange(
                    "Removed/Removed.csproj",
                    CodeGraphSourceChangeKind.Deleted,
                    "sha256:previous",
                    null)
            ]);
        var plugin = new CSharpCodeGraphPlugin();
        var sink = new RecordingSink();

        await using var session = await plugin.CreateSessionAsync(context);
        var result = await session.ExtractAsync(sink);

        Assert.Contains(
            result.ObsoleteIndexUnits,
            unit => unit.Value == CSharpProjectUnitId("Removed/Removed.csproj"));
    }

    [Fact]
    public async Task ExtractAsync_EmitsInheritanceImplementsAndCallsEdges()
    {
        var extracted = await ExtractAsync(
            ("src/Greeter.cs", """
                using System;
                namespace Example;

                public interface IGreeter { string Greet(); }

                public abstract class BaseGreeter
                {
                    public abstract string Core();
                }

                public class Greeter : BaseGreeter, IGreeter
                {
                    private readonly int _seed;
                    public Greeter(int seed) => _seed = seed;
                    public override string Core() => _seed.ToString();
                    public string Greet() => $"greet:{Core()}";
                    public static void Announce(Greeter value) => Console.WriteLine(value.Greet());
                }
                """));

        var greeter = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Example.Greeter");
        var interfaceNode = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Interface && node.QualifiedName == "Example.IGreeter");
        var baseNode = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Example.BaseGreeter");
        var greet = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.Greeter.Greet()");
        var coreOverride = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName?.StartsWith("Example.Greeter.Core()", StringComparison.Ordinal) == true);

        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Inherits &&
            edge.SourceId == greeter.Id &&
            edge.TargetId == baseNode.Id);
        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Implements &&
            edge.SourceId == greeter.Id &&
            edge.TargetId == interfaceNode.Id);
        // Roslyn correctly binds Greet()'s Core() call to the override on
        // Greeter, not the abstract declaration on BaseGreeter.
        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Calls &&
            edge.SourceId == greet.Id &&
            edge.TargetId == coreOverride.Id);
    }

    [Fact]
    public async Task ExtractAsync_EmitsReferencesImportsAndCoverage()
    {
        var extracted = await ExtractAsync(
            ("src/Hub.cs", """
                namespace Example;

                public enum Mode { Fast, Slow }

                public class Hub
                {
                    public const int Limit = 42;
                    public Mode Current { get; set; }

                    /// <summary>Central dispatch point.</summary>
                    [System.Obsolete("use Hub2")]
                    public void Dispatch(Mode mode)
                    {
                        var kind = typeof(Mode);
                        if (mode == Mode.Fast) { }
                    }
                }
                """),
            ("src/Consumer.cs", """
                namespace Example;

                public class Consumer
                {
                    public Hub Target() => new Hub();
                }
                """));

        var hub = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Example.Hub");
        var dispatch = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.Hub.Dispatch(Example.Mode)");
        var limit = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Field && node.Name == "Limit");

        // Tier-A ride-alongs: doc summary and attributes on the method that
        // carries them; constant literal on the field.
        Assert.IsType<CodeTextProperty>(dispatch.Properties["doc-summary"]);
        Assert.Contains(
            "Central dispatch",
            ((CodeTextProperty)dispatch.Properties["doc-summary"]).Value);
        Assert.True(
            dispatch.Properties.ContainsKey("obsolete"),
            $"obsolete not found; keys=[{string.Join(",", dispatch.Properties.Keys)}]");
        Assert.True(limit.Properties.TryGetValue("constant-value", out var literal));
        Assert.Equal(42, ((CodeIntegerProperty)literal).Value);

        // References: typeof + constant usage from the same callable.
        Assert.Contains(
            extracted.Edges,
            edge => edge.Kind == CodeEdgeKinds.References &&
                edge.SourceId != edge.TargetId);

        var coverage = extracted.Result.RelationshipCoverage.ToArray();
        Assert.Contains(coverage, value =>
            value.RelationshipKind == CodeEdgeKinds.Returns.Value &&
            value.State == CodeRelationshipCoverageState.NotProduced);
        Assert.Contains(coverage, value =>
            value.RelationshipKind == CodeEdgeKinds.Accepts.Value &&
            value.State == CodeRelationshipCoverageState.NotProduced);
        Assert.Contains(coverage, value =>
            value.RelationshipKind == CodeEdgeKinds.Imports.Value);
        Assert.All(coverage, value =>
            Assert.True(CodeRelationshipCoverageState.IsDefined(value.State)));
    }

    [Fact]
    public async Task ExtractAsync_UnresolvedCallTargetsAreCountedNotGuessed()
    {
        var extracted = await ExtractAsync(
            ("src/Broken.cs", """
                namespace Example;

                public class Caller
                {
                    public void Run()
                    {
                        MissingLibrary.DoWork();
                    }
                }
                """));

        var coverage = extracted.Result.RelationshipCoverage.Single(value =>
            value.RelationshipKind == CodeEdgeKinds.Calls.Value);
        Assert.Equal(CodeRelationshipCoverageState.Partial, coverage.State);
        Assert.True(coverage.UnresolvedTargets > 0);
        Assert.DoesNotContain(
            extracted.Edges,
            edge => edge.Kind == CodeEdgeKinds.Calls &&
                extracted.Nodes.Single(node => node.Id == edge.TargetId).QualifiedName!
                    .Contains("MissingLibrary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_CrossProjectCallsResolveToDependencySymbols()
    {
        var extracted = await ExtractAsync(
            ("src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Lib/Utility.cs", """
                namespace Lib;
                public static class Utility
                {
                    public static int Add(int a, int b) => a + b;
                }
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                namespace App;
                using Lib;
                public class Program
                {
                    public int Sum() => Utility.Add(1, 2);
                }
                """));

        var add = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName!.Contains("Utility.Add(", StringComparison.Ordinal));
        var sum = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "App.Program.Sum()");

        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Calls &&
            edge.SourceId == sum.Id &&
            edge.TargetId == add.Id);
    }

    private static async Task<Extraction> ExtractAsync(
        params (string Path, string Content)[] values)
    {
        var sources = values.Select(value => new CodeGraphSource(
            value.Path,
            $"sha256:{Hash(value.Content)}",
            _ => new ValueTask<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(value.Content))))).ToArray();
        var context = new CodeGraphPluginContext(
            new CodeRepositoryId("repo:test"),
            "memory://test",
            new CodeIndexRunId("run:test"),
            sources);
        var plugin = new CSharpCodeGraphPlugin();
        var sink = new RecordingSink();

        await using var session = await plugin.CreateSessionAsync(context);
        var result = await session.ExtractAsync(sink);

        Assert.True(sink.Batches.Count > 0);
        Assert.True(sink.Batches[^1].CompletesIndexUnit);
        Assert.All(sink.Batches, batch =>
        {
            Assert.Equal(plugin.Id, batch.Origin.PluginId);
            Assert.StartsWith("csharp:project:", batch.Origin.IndexUnitId.Value, StringComparison.Ordinal);
        });
        return new(
            sink.Batches.SelectMany(batch => batch.Nodes).OrderBy(node => node.Id.Value, StringComparer.Ordinal).ToArray(),
            sink.Batches.SelectMany(batch => batch.Declarations).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray(),
            sink.Batches.SelectMany(batch => batch.Edges).OrderBy(edge => edge.Id.Value, StringComparer.Ordinal).ToArray(),
            result,
            sink.Batches.Select(batch => batch.Origin.IndexUnitId).Distinct().ToArray());
    }

    private static string Hash(string content) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string CSharpProjectUnitId(string projectPath) =>
        $"csharp:project:{Hash(projectPath)}";

    private sealed class RecordingSink : ICodeGraphSink
    {
        public CodeGraphBatchLimits Limits { get; } = new();
        public List<CodeGraphBatch> Batches { get; } = [];

        public ValueTask WriteBatchAsync(
            CodeGraphBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Batches.Add(batch);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Extraction(
        IReadOnlyList<CodeGraphNode> Nodes,
        IReadOnlyList<CodeGraphDeclaration> Declarations,
        IReadOnlyList<CodeGraphEdge> Edges,
        CodeGraphExtractionResult Result,
        IReadOnlyList<CodeIndexUnitId> UnitIds);
}
