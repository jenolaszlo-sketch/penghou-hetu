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

        Assert.Equal(
            [typeof(HetuHostBuilderExtensions), typeof(CSharpCodeGraphPlugin)],
            exported.OfType<Type>().OrderBy(type => type.FullName).ToArray());
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
        Assert.IsType<CodeTextProperty>(dispatch.Properties[CodePropertyKeys.DocSummary]);
        Assert.Contains(
            "Central dispatch",
            ((CodeTextProperty)dispatch.Properties[CodePropertyKeys.DocSummary]).Value);
        Assert.True(
            dispatch.Properties.ContainsKey(CodePropertyKeys.Obsolete),
            $"obsolete not found; keys=[{string.Join(",", dispatch.Properties.Keys)}]");
        Assert.True(limit.Properties.TryGetValue(CodePropertyKeys.ConstantValue, out var literal));
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

    [Fact]
    public async Task ExtractAsync_ResolvesGenericMethodCallsToTheirDefinition()
    {
        var extracted = await ExtractAsync(
            ("src/Store.cs", """
                namespace Example;

                public class Store<T>
                {
                    public T Get(T fallback) => fallback;
                    public int Count<TItem>(TItem item) => 1;
                }

                public class Driver
                {
                    public string Run()
                    {
                        var store = new Store<string>();
                        return store.Get("hi");
                    }
                }
                """));

        // Constructed generics resolve back to the generic definition node.
        var get = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Callable &&
                node.QualifiedName!.Contains("Store<T>.Get(", StringComparison.Ordinal));
        var count = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Callable &&
                node.QualifiedName!.Contains("Store<T>.Count<", StringComparison.Ordinal));
        var run = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.Driver.Run()");

        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Calls &&
            edge.SourceId == run.Id &&
            edge.TargetId == get.Id);
        Assert.Equal(
            CodeRelationshipCoverageState.Produced,
            extracted.Result.RelationshipCoverage.Single(value =>
                value.RelationshipKind == CodeEdgeKinds.Calls.Value).State);
    }

    [Fact]
    public async Task ExtractAsync_ResolvesExtensionMethodCallsToTheirDefinition()
    {
        var extracted = await ExtractAsync(
            ("src/Extensions.cs", """
                namespace Example;

                public static class StringExtensions
                {
                    public static string Shout(this string value) => value.ToUpperInvariant();
                }

                public class Announcer
                {
                    public string Run(string input) => input.Shout();
                }
                """));

        // Instance-syntax extension calls resolve to the static definition.
        var shout = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Callable &&
                node.QualifiedName!.Contains("StringExtensions.Shout(", StringComparison.Ordinal));
        var run = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.Announcer.Run(string)");

        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Calls &&
            edge.SourceId == run.Id &&
            edge.TargetId == shout.Id);
    }

    [Fact]
    public async Task ExtractAsync_ResolvesInterfaceDispatchToTheInterfaceMember()
    {
        var extracted = await ExtractAsync(
            ("src/Dispatch.cs", """
                namespace Example;

                public interface IWorker { void Execute(); }

                public class Worker : IWorker
                {
                    public void Execute() { }
                }

                public class Supervisor
                {
                    public void Run(IWorker worker) => worker.Execute();
                }
                """));

        var worker = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Type && node.QualifiedName == "Example.Worker");
        var interfaceNode = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Interface && node.QualifiedName == "Example.IWorker");
        var interfaceExecute = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.IWorker.Execute()");
        var run = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable &&
            node.QualifiedName == "Example.Supervisor.Run(Example.IWorker)");

        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Implements &&
            edge.SourceId == worker.Id &&
            edge.TargetId == interfaceNode.Id);
        // The receiver is interface-typed, so Roslyn binds to the interface
        // member rather than guessing an implementation.
        Assert.Contains(extracted.Edges, edge =>
            edge.Kind == CodeEdgeKinds.Calls &&
            edge.SourceId == run.Id &&
            edge.TargetId == interfaceExecute.Id);
    }

    [Fact]
    public async Task ExtractAsync_EmitsPackageReferenceNodesWithSyntaxEvidence()
    {
        var extracted = await ExtractAsync(
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
                    <PackageReference Include="Serilog" Version="4.0.0" Condition="'$(TargetFramework)' == 'net10.0'" />
                    <PackageReference Include="Local.Tool" />
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                namespace App;
                public class Program
                {
                    public void Run() { }
                }
                """));

        var project = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Project);
        var newtonsoft = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Package && node.Name == "Newtonsoft.Json");
        Assert.Equal(
            "13.0.1",
            ((CodeTextProperty)newtonsoft.Properties[CodePropertyKeys.PackageVersion]).Value);
        var serilog = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Package && node.Name == "Serilog");
        // Conditions are preserved unexpanded: no MSBuild evaluation.
        Assert.Equal(
            "'$(TargetFramework)' == 'net10.0'",
            ((CodeTextProperty)serilog.Properties[CodePropertyKeys.PackageCondition]).Value);
        var unversioned = Assert.Single(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Package && node.Name == "Local.Tool");
        Assert.Equal(
            string.Empty,
            ((CodeTextProperty)unversioned.Properties[CodePropertyKeys.PackageVersion]).Value);

        var packageEdges = extracted.Edges
            .Where(edge =>
                edge.Kind == CodeEdgeKinds.DependsOn &&
                edge.SourceId == project.Id)
            .ToArray();
        Assert.Equal(3, packageEdges.Length);
        Assert.All(packageEdges, edge =>
            Assert.Equal(CodeEvidenceKind.Syntax, edge.Evidence.Kind));
        Assert.Contains(packageEdges, edge => edge.TargetId == newtonsoft.Id);
    }

    [Fact]
    public async Task ExtractAsync_CapsPackageReferencesDeterministically()
    {
        var references = string.Join(
            "\n",
            Enumerable.Range(0, 257).Select(index =>
                $"""<PackageReference Include="Pkg{index:D3}" Version="1.0.0" />"""));
        var extracted = await ExtractAsync(
            ("src/App/App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                {references}
                  </ItemGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                namespace App;
                public class Program
                {
                    public void Run() { }
                }
                """));

        Assert.Equal(
            256,
            extracted.Nodes.Count(node => node.Kind == CodeNodeKinds.Package));
        Assert.Contains("csharp.project.package-cap", extracted.Result.WarningCodes);
    }

    [Fact]
    public async Task ExtractAsync_SolutionDefinesTheCanonicalProjectSet()
    {
        var extracted = await ExtractAsync(
            ("src/App.sln", """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Ghost", "Ghost\\Ghost.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                EndGlobal
                """),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/App/Program.cs", """
                namespace App;
                public class Program
                {
                    public void Run() { }
                }
                """),
            ("src/Orphan/Orphan.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("src/Orphan/Orphan.cs", """
                namespace Orphan;
                public class Orphan
                {
                    public void Run() { }
                }
                """));

        // Listed projects index; unlisted projects are skipped (never merged
        // into loose sources); listed-but-absent projects warn.
        var unitIds = extracted.UnitIds.Select(unit => unit.Value).ToArray();
        Assert.Contains(CSharpProjectUnitId("src/App/App.csproj"), unitIds);
        Assert.DoesNotContain(CSharpProjectUnitId("src/Orphan/Orphan.csproj"), unitIds);
        Assert.Contains(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Type && node.QualifiedName == "App.Program");
        Assert.DoesNotContain(
            extracted.Nodes,
            node => node.QualifiedName == "Orphan.Orphan");
        Assert.Contains("csharp.solution.unlisted-project", extracted.Result.WarningCodes);
        Assert.Contains("csharp.solution.missing-project", extracted.Result.WarningCodes);
    }

    [Fact]
    public async Task ExtractAsync_MarksExactAllowlistedTestMethods()
    {
        var extracted = await ExtractAsync(
            ("src/Tests.cs", """
                namespace Xunit
                {
                    public class FactAttribute : System.Attribute { }
                    public class CustomFactAttribute : System.Attribute { }
                }

                namespace Example;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }

                public class CalculatorTests
                {
                    [Xunit.Fact]
                    public void AddWorks()
                    {
                        var calculator = new Calculator();
                        calculator.Add(1, 2);
                    }

                    [Xunit.CustomFact]
                    public void CustomLabeled() { }

                    public void Helper() { }
                }
                """));

        var test = extracted.Nodes.Single(node =>
            node.Kind == CodeNodeKinds.Callable && node.Name == "AddWorks");
        Assert.True(
            test.Properties.TryGetValue(CodePropertyKeys.TestMethod, out var marker) &&
            marker is CodeBooleanProperty { Value: true });
        // Near-miss attribute names and plain methods are never tests.
        Assert.DoesNotContain(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Callable &&
                node.Name is "CustomLabeled" or "Helper" &&
                node.Properties.ContainsKey(CodePropertyKeys.TestMethod));
        Assert.DoesNotContain(
            extracted.Nodes,
            node => node.Kind == CodeNodeKinds.Callable &&
                node.Name == "Add" &&
                node.Properties.ContainsKey(CodePropertyKeys.TestMethod));
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

