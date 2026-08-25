# Penghou.Hetu

Penghou.Hetu is an embedded, language-neutral code knowledge graph for .NET.
It turns source repositories into a normalized, queryable graph for repository
understanding, dependency discovery, impact analysis, and context selection.

Hetu defines what code means in the graph. Language plugins define how those
facts are discovered.

```text
Source repository
       │
       ▼
Language extraction session
       │
       ▼
Owned graph-fact batches
       │
       ▼
Hetu validation and persistence
       │
       ▼
Bounded deterministic queries
       │
       ▼
Solo and other consumers
```

Hetu does not define a universal AST and does not expose parser-specific types.
A C# plugin may use Roslyn while future plugins may use compiler APIs, generated
ANTLR parsers, hand-written parsers, or other deterministic extraction tools.

## Status

Hetu has a working preview runtime, C# extractor, durable Ladybug provider, and
shared provider conformance suite. The API remains preview-quality and is
currently built for .NET 10. See [ROADMAP.md](ROADMAP.md) for the remaining
semantic milestones and first-release invariants.

## Planned packages

| Package | Purpose |
| --- | --- |
| `Penghou.Hetu.Abstractions` | Parser-independent graph vocabulary, evidence, extraction sessions, and sinks |
| `Penghou.Hetu` | Indexing orchestration, validation, in-memory storage, and query services |
| `Penghou.Hetu.CSharp` | Roslyn-based C# extraction plugin |
| `Penghou.Hetu.Ladybug` | Embedded LadybugDB graph-store provider |
| `Penghou.Hetu.Testing` | Reusable graph-store and plugin contract tests |

The future `Penghou.Hetu.Generator` project is reserved for deterministic
generation of language plugins from grammars and declarative graph mappings.
It is intentionally outside the first milestone.

## Runtime foundation

The current runtime supports deterministic plugin registration, strict bounded
batch ingestion, run-scoped atomic publication, repository and index-run
manifests, exact symbol/declaration lookup, and bounded graph traversal:

```csharp
var store = new InMemoryCodeGraphStore();
var repositoryId = new CodeRepositoryId("repo:my-app");
var runId = new CodeIndexRunId("run:initial");
var pluginId = new CodePluginId("hetu-csharp");

await store.UpsertRepositoryAsync(new CodeRepositoryManifest(repositoryId));
await store.StoreIndexRunAsync(new CodeIndexRunManifest(
    repositoryId,
    runId,
    DateTimeOffset.UtcNow,
    plugins: [pluginId]));

await using var sink = new CodeGraphIngestionSink(store);
// Extraction sessions stream owned CodeGraphBatch values to the sink.
```

`Penghou.Hetu.Testing` contains a reusable conformance suite that every durable
store provider must pass.

Repository discovery is provider-based. Local filesystem discovery is
registered by default, while hosts can add a VFS, Git tree, archive, remote
workspace, or in-memory implementation without changing language plugins:

```csharp
var sources = new HetuBuilder()
    .AddRepositoryProvider(new MyVirtualRepositoryProvider())
    .BuildRepositoryProviderRegistry();

await using var repository = await sources.OpenAsync(
    new CodeRepositoryDescriptor(
        new CodeRepositoryId("repo:my-app"),
        "vfs://workspaces/my-app"));
```

Use `ClearRepositoryProviders()` when a host wants to replace the default
filesystem provider rather than extend it. Ambiguous provider claims produce an
explicit error instead of being resolved by registration order.

Repository discovery can now be converted into a deterministic incremental
index plan. The planner uses provider content hashes when available and computes
SHA-256 hashes otherwise. It compares those inputs with prior source manifests and
classifies each selected source as new, changed, unchanged, or deleted; changing
a plugin version invalidates that plugin's otherwise unchanged units:

```csharp
var plugins = new HetuBuilder()
    .AddPlugin(new MyLanguagePlugin())
    .BuildPluginRegistry();
var planner = new CodeIndexPlanner(plugins);

CodeIndexPlan plan = await planner.CreatePlanAsync(
    repository,
    previousManifests,
    new CodeIndexPlanningOptions(pluginIds: [new("my-language")]));
```

Repository IDs remain caller-supplied and should identify the logical repository
across checkouts and machines. A host that lacks such an identity may derive one
from a canonical local path, but that fallback is deliberately host-local and
must not be treated as a portable repository identity.

Source manifests report changed inputs; they do not dictate extraction
ownership. A syntax plugin may emit one index unit per file, while a semantic
plugin may atomically own a project or solution unit. Plugin contexts therefore
provide repeatable `CodeGraphSource` readers and only an optional provider-defined
location hint—plugins must not assume repositories live on a local filesystem.

`CodeIndexingService` now coordinates the first complete lifecycle: repository
opening, source planning, plugin execution, bounded ingestion, obsolete-unit
cleanup, and atomic publication of the latest successful source state. Hosts
supply the run identity, making retries and telemetry correlation explicit:

```csharp
var indexing = new CodeIndexingService(repositoryProviders, plugins, store);
var result = await indexing.IndexAsync(
    new CodeRepositoryDescriptor(
        new CodeRepositoryId("repo:my-app"),
        repositoryLocation),
    new CodeIndexRunId("run:2026-08-25T12:00:00Z"),
    new CodeIndexingOptions(maxConcurrentPlugins: 2));
```

Plugins see the complete current source set plus exact source transitions. They
remain responsible for choosing atomic index units and report obsolete unit IDs
after extraction. Hetu refuses to complete a run if any streamed unit remains
buffered or rejected.

Lifecycle execution is defensive at repository boundaries. Each executing
plugin receives a scoped sink, so it cannot accidentally emit facts under a
different repository, run, plugin identity, or plugin version. Plugins whose
inputs are entirely unchanged are not invoked.

For providers without an immutable snapshot, Hetu hashes sources during
planning and materializes bounded, repeatable content before extraction. A
SHA-256 mismatch means the live source changed during the boundary and the run
fails explicitly. `CodeIndexingOptions` limits both individual source size and
total bytes read; defaults are 16 MiB per source and 512 MiB per run. Discovery
diagnostics report unsupported files, excluded and depth-limited directories,
skipped reparse points, and source bytes without recording paths or content.

Language plugins return bounded, privacy-safe extraction diagnostics alongside
obsolete unit IDs. They can report examined and contributing source counts,
unresolved relationship counts, and stable warning codes. Lifecycle diagnostics
add per-plugin status, version, duration, source counts, unresolved counts, and
obsolete-unit counts, while aggregate diagnostics make repository-level health
visible without logging source paths or content. Diagnostic callbacks remain
failure-isolated from indexing behavior.

## C# structural extraction

`Penghou.Hetu.CSharp` now provides the first useful Roslyn plugin. It consumes
provider-neutral source readers, discovers SDK-style `.csproj` inputs without
requiring a physical filesystem or MSBuild, creates one compilation and atomic
index unit per project, and emits projects, files, namespaces, types,
interfaces, delegates, callables,
properties, fields, parameters, physical declarations, and semantic containment:

```csharp
var plugin = new CSharpCodeGraphPlugin();
var indexing = new CodeIndexingService(
    repositoryProviders,
    new CodeGraphPluginRegistry([plugin]),
    store);

await indexing.IndexAsync(
    new CodeRepositoryDescriptor(repositoryId, repositoryLocation),
    runId);
```

Partial declarations share one semantic symbol node while retaining separate
source declarations. Callable identities distinguish overload signatures. The
plugin reports stable Roslyn diagnostic codes rather than compiler messages or
source content.

Project discovery supports default compile items, explicit compile includes and
removals, linked sources, common compiler properties, and project references.
Sources outside project directories fall into a deterministic loose-source
project. Project-reference compilations and `depends-on` edges are created in
dependency order, while missing and cyclic references are diagnosed rather
than guessed. This deliberately lightweight model does not evaluate MSBuild
conditions, imports, custom targets, or solution configurations; full MSBuild
evaluation remains a future opt-in provider concern.

The plugin version comes from its package informational version, so package
updates naturally invalidate prior source manifests. Unresolved relationship
counts use a conservative set of missing symbol, namespace, member, and assembly
diagnostics; syntax errors remain visible as warning codes without being
misclassified as relationship failures.

The lightweight plugin currently builds Roslyn platform references from the
host runtime's trusted platform assemblies. This keeps extraction independent
of MSBuild, but a project targeting another framework may be analyzed against
the host's .NET 10 reference surface. Hosts requiring exact target-framework
semantics should treat this as a known preview limitation; a future opt-in
reference resolver can supply targeting-pack metadata without changing Hetu's
graph contracts.

## Provider-neutral queries

`CodeGraphQueryService` provides exact symbol and declaration lookup plus
bounded references, callers, callees, implementations, dependencies,
dependents, neighborhoods, and impact sets. Exact qualified-name lookup retains
all candidates and reports ambiguity instead of selecting one implicitly.
Traversal limits and evidence-kind filters are applied by the store, so filtered
queries cannot exceed their declared node, edge, or depth budgets.

Provenance-aware query variants return a `CodeGraphQueryEnvelope<T>` binding
the result to one successful publication and the exact applied query. Returned
nodes, declarations, and edges include every contributing plugin version and
index-unit origin. Traversal results also report the truncation reason, depth
reached, examined counts, and omitted frontier count.

## Ladybug persistence

`Penghou.Hetu.Ladybug` provides `LadybugCodeGraphStore`, an embedded durable
implementation of the same `ICodeGraphStore` contract. It uses the official
LadybugDB 0.19.1 managed binding, validates its schema version on open, persists
mutations transactionally, restores state after process restart, and exposes a
lightweight health result. Hosts must reference one matching native LadybugDB
runtime package, such as `LadybugDB.Native.win-x64`, or the all-platform
`LadybugDB.Native` meta-package. Native binaries are deliberately not forced on
consumers by the provider package. The current Windows engine also requires the
OpenSSL 3 runtime libraries (`libcrypto-3-x64.dll` and `libssl-3-x64.dll`) to be
available to the host process.

## Architectural boundaries

- Core abstractions have no Roslyn, ANTLR, LadybugDB, LSP, SCIP, or AI dependency.
- Plugins receive repository-aware extraction sessions so semantic analyzers can
  resolve project-wide and cross-file relationships.
- Plugins emit normalized facts; they never write directly to a graph database.
- Hetu owns graph vocabulary, validation, physical identity, transactions,
  incremental replacement, and query semantics.
- Persistence providers implement Hetu's store contract without leaking their
  query language through ordinary consumer APIs.
- All traversals are bounded and deterministically ordered.
- Evidence records how each relationship was discovered without overstating
  syntax-only or heuristic information as compiler-resolved truth.

## Intended use

Hetu will help answer questions such as:

- What does this type contain?
- What calls this method, and what does it call?
- Which types implement this interface?
- What references or depends on this symbol?
- What bounded neighborhood is likely affected by a change?
- Which source declarations should Solo include in model context?

## Relationship to the Penghou stack

```text
Baize   provider and model communication
Nuwa    deterministic structured-output repair
Zhinu   durable workflow execution
Cangjie durable contextual knowledge and provenance
Hetu    source-code structure, relationships, and impact
Solo    software-engineering orchestration and interpretation
```

Hetu may supply graph-derived observations to Cangjie or Solo, but it does not
become a general memory store or workflow engine.

## Initial non-goals

- universal compilation or exhaustive data-flow analysis;
- runtime AI parsing or plugin generation;
- embeddings or vector search;
- automatic plugin or NuGet downloading;
- LSP, SCIP, or Tree-sitter integration;
- AI-generated architectural labels or component clustering;
- generated non-C# language packages.

The first useful release will index real C# repositories, persist a stable
graph, survive repeat and incremental indexing, and answer bounded structural
and dependency queries.
