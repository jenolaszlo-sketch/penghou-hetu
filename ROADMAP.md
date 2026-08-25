# Penghou.Hetu Roadmap

## Objective

Build a local-first, language-neutral code knowledge graph that gives Solo and
other .NET consumers deterministic repository comprehension without coupling
the graph model to Roslyn, ANTLR, LadybugDB, or any model provider.

The central invariant is:

> Hetu defines what code means in the graph. Plugins define how those facts are
> discovered.

## Architectural laws

### Repository-aware extraction

Extraction is session- and batch-oriented rather than fundamentally
file-at-a-time. A semantic plugin may construct a project or solution model and
resolve relationships across all selected files. Syntax-only plugins may still
process individual files within the same contract.

### Symbols and declarations are distinct

A semantic symbol has a stable identity independent of any one physical
declaration. Declarations carry source locations and belong to files. This
supports partial types, generated code, file moves, and safe incremental
replacement without pretending that one symbol has exactly one location.

### Hetu owns physical identity

Plugins provide canonical language identities. Hetu scopes and encodes them
using repository identity, language, symbol kind, and canonical key. File paths
identify physical declarations and index units; they are not used as semantic
symbol identity when a language provides a stronger key.

Repository identity, checkout state, index run, plugin identity, and plugin
version are separate concepts.

### Every fact has an owner and evidence

Every persisted node contribution and edge identifies the plugin and index unit
that produced it. Non-trivial relationships carry evidence describing whether
they came from syntax, semantic resolution, a heuristic, or future AI
inference. Deterministic evidence does not receive invented confidence scores.

### Replacement is atomic

Plugins emit bounded batches. Hetu validates a batch and atomically replaces
the facts owned by its index unit. Failed extraction or validation cannot leave
a half-updated graph. Repeating an equivalent batch is idempotent.

### Persistence is replaceable

The store contract is provider-neutral. The in-memory and Ladybug providers
must pass the same conformance suite. Plugins never access a store directly,
and ordinary query APIs do not expose provider query languages.

### Queries are bounded and deterministic

Traversal APIs require depth and result limits, handle cycles, expose evidence,
and return stable ordering. Hetu reports ambiguity rather than choosing a fuzzy
symbol match silently.

## Graph vocabulary for the first release

Initial semantic node kinds:

```text
repository  project  package  file  namespace
type  interface  callable  property  field  parameter
```

Physical declaration records associate semantic symbols with one or more
source locations.

Initial edge kinds:

```text
contains  declares  references  calls
implements  inherits  imports  depends-on
returns  accepts
```

`reads` and `writes` remain reserved until their cross-language meaning and
evidence requirements are proven. Kinds use extensible string-backed value
types with well-known constants rather than closed enums.

Graph properties are restricted to a deterministic portable value model.
Arbitrary `object` values are not accepted.

## Milestone 1 — contracts and semantic invariants (completed)

Define in `Penghou.Hetu.Abstractions`:

- repository, plugin, index-unit, symbol, declaration, node, and edge identity;
- string-backed node and edge kinds with well-known constants;
- portable graph-property values;
- source locations and extraction origins;
- syntax, semantic, heuristic, and future AI evidence categories;
- plugin capabilities;
- repository-aware plugin and extraction-session contracts;
- bounded batch sink contracts and validation errors.

Add public API snapshot tests and serialization round-trip tests. Keep this
package free from parser, database, hosting, and model dependencies.

Exit criteria:

- a syntax-only fake plugin and a project-aware fake plugin can use the same
  contracts;
- partial declarations and cross-file relationships are representable;
- every fact can be attributed to one extraction owner;
- invalid property values and malformed identities are rejected explicitly.

Delivered in the initial contract slice:

- validated repository, plugin, run, index-unit, node, symbol, declaration,
  and edge identities;
- extensible node and edge kinds with well-known constants;
- deterministic typed property values rather than arbitrary objects;
- one-based repository-relative source locations;
- syntax, semantic, heuristic, and reserved AI evidence with honest confidence
  rules;
- semantic symbols with multiple physical declarations;
- repository-aware, repeatable-source extraction sessions;
- owned, bounded, incrementally streamed batches with explicit completion;
- per-batch and per-index-unit work limits so repeated small batches cannot
  evade ingestion bounds;
- source-content-free validation error contracts;
- serialization, public API, dependency-boundary, malformed-input,
  partial-symbol, cross-file, and syntax/project-aware plugin tests.

## Milestone 2 — atomic in-memory runtime (completed)

Implement in `Penghou.Hetu`:

- plugin registration and deterministic selection;
- graph-batch validation;
- atomic index-unit replacement;
- an in-memory store;
- repository and index-run manifests;
- exact lookup and bounded traversal primitives;
- privacy-safe indexing diagnostics.

Implement `Penghou.Hetu.Testing` with a reusable store conformance suite.

Exit criteria:

- equivalent re-indexing creates no duplicates;
- changed and deleted index units remove only their owned facts;
- shared semantic nodes survive removal of one contributing declaration;
- cancellation and failed validation leave the previous graph intact;
- traversal limits and deterministic ordering are covered by tests.

Delivered:

- provider-neutral repository, index-run, replacement, lookup, and bounded
  traversal store contracts;
- strict batch validation for counts, duplicate identities, property sizes,
  list sizes, and per-index-unit cumulative work;
- streamed ingestion that keeps incomplete or rejected units invisible;
- atomic in-memory replacement with per-owner contributions;
- shared semantic-node survival across partial declaration removal;
- exact node, symbol, qualified-name, and declaration lookup;
- cycle-safe traversal with direction, edge-kind, depth, node, and edge bounds;
- deterministic plugin registration and explicit ambiguity errors;
- idempotent manifest and replacement retries with running-to-terminal run
  transitions;
- privacy-safe ingestion counts and bounded warning codes;
- a reusable provider conformance suite covering manifests, atomicity,
  cancellation, idempotency, ownership, shared nodes, and traversal.

## Milestone 3 — repository discovery and indexing lifecycle

Completed repository-source slice:

- provider-neutral repository descriptors, entries, lazy enumeration, and
  content-opening contracts;
- a default local filesystem provider with normalized repository-relative
  paths, deterministic enumeration, configurable directory exclusions, depth
  and entry limits, and conservative reparse-point handling;
- consistent-snapshot and provider snapshot-identity signals for future Git,
  VFS, archive, IDE, and remote-workspace implementations;
- deterministic provider registration through `HetuBuilder`, including custom
  providers, complete default replacement, explicit missing-provider errors,
  and ambiguity detection;
- filesystem and custom in-memory provider regression tests.

Completed incremental-planning slice:

- deterministic source-to-plugin selection with optional explicit plugin
  filtering and validation of unknown plugin identities;
- SHA-256 content hashing when a provider does not supply a trustworthy content
  identity, while preserving provider hashes without reopening content;
- stable plugin-and-path-derived index-unit identities;
- portable per-unit manifests containing plugin version, source path, and source
  hash;
- deterministic new, changed, unchanged, and deleted classifications, including
  plugin-version invalidation;
- explicit failure for ambiguous plugin claims and duplicate repository entries;
- cancellation and incremental-planning regression tests.

Lifecycle boundary hardening:

- source-change manifests are distinct from plugin-defined atomic index units,
  so project- and solution-aware plugins are not forced into file ownership;
- plugin contexts expose only an optional provider location hint and repeatable
  source handles, without requiring a physical filesystem root;
- stores atomically publish the latest successful source/plugin/snapshot state
  with run completion, while failed and cancelled runs retain prior state;
- the provider conformance suite verifies successful incremental-state
  publication and round-tripping.

Completed lifecycle-execution foundation:

- provider-neutral orchestration from repository opening through incremental
  planning, extraction, ingestion, cleanup, and successful state publication;
- exact new, changed, unchanged, and deleted source transitions supplied to
  plugins alongside the complete current source set;
- plugin-owned obsolete index-unit declarations, avoiding file-to-unit
  assumptions for project- and solution-aware analyzers;
- configurable bounded plugin concurrency and existing bounded batch ingestion;
- completed, failed, and cancelled run transitions that retain the last
  successful incremental state on failure;
- privacy-safe lifecycle counts and planning, extraction, and persistence
  timings with failure-isolated observers;
- an explicit terminal ingestion check so incomplete buffered units cannot be
  discarded while a run is reported successful;
- end-to-end tests for successful indexing, deleted-source cleanup, failed-run
  state retention, diagnostics isolation, and incomplete-unit rejection.

Completed pre-Roslyn lifecycle hardening:

- lifecycle-scoped sinks reject batches whose repository, run, plugin, or
  plugin version differs from the executing plugin, while stores independently
  require replacement plugins to belong to the active run;
- fully unchanged plugins skip extraction and source materialization while
  retaining their prior graph and successful source state;
- sources used by plugins are bounded, repeatable in-memory snapshots rather
  than a second unverified view of a live filesystem;
- SHA-256 sources are checked between planning and materialization, and a
  boundary change fails explicitly instead of indexing bytes under a stale hash;
- configurable per-source and total byte limits apply during both hashing and
  materialization;
- diagnostics distinguish unsupported files, excluded directories,
  depth-limited directories, skipped reparse points, and bytes read;
- regression tests cover ownership violations, unchanged-plugin skipping,
  source races, per-source and total byte limits, and bounded parallel plugins.

Completed Milestone 4 preparation:

- bounded extraction results report sources examined, sources contributing
  facts, unresolved relationships, obsolete units, and privacy-safe warning
  codes;
- lifecycle diagnostics expose deterministically ordered per-plugin status,
  version, duration, supplied/examined/contributing source counts, unresolved
  relationships, obsolete-unit counts, and warning codes;
- failed and cancelled plugin executions remain observable without replacing
  the original exception or allowing telemetry observers to affect indexing;
- aggregate unresolved counts and warning codes are available without logging
  source paths or content;
- validation, serialization, success, and failure regression tests lock down the
  diagnostics contract before the Roslyn plugin depends on it.

Implement:

- stable caller-supplied repository identity with a documented local fallback;
- configurable traversal and plugin selection;
- default exclusions for `.git`, `bin`, `obj`, `node_modules`, `dist`,
  `packages`, and `vendor`;
- file hashing and plugin-version manifests;
- new, changed, unchanged, and deleted index-unit planning;
- bounded concurrency and cancellation;
- indexing metrics and rejected-fact diagnostics.

Initial diagnostics include files discovered, excluded, skipped, indexed, and
deleted; nodes and edges produced; unresolved relationships; rejected facts;
plugin duration; persistence duration; and index-run identity.

## Milestone 4 — useful Roslyn C# plugin

Implement `Penghou.Hetu.CSharp` without leaking Roslyn types into public core
contracts.

First structural slice:

- solution and project discovery where practical;
- files, namespaces, classes, structs, records, interfaces, and enums;
- methods, constructors, properties, fields, and parameters;
- semantic symbols separated from declarations;
- contains and declares relationships.

Second semantic slice:

- inherits and implements;
- calls and references where Roslyn resolves a unique target;
- project dependencies and imports;
- unresolved-reference diagnostics without guessed targets.

Use canonical C# identities that account for containing symbols, metadata
names, generic arity, overload signatures, parameter types, and member kind.

Completed initial structural slice:

- a public `CSharpCodeGraphPlugin` backed by Roslyn 5.9 without exposing Roslyn
  types through Hetu contracts;
- provider-neutral parsing of supplied repeatable `.cs` sources without a
  filesystem or `MSBuildWorkspace` requirement;
- repository-wide semantic compilation using stable documentation identities
  and hashed Hetu node, symbol, declaration, and edge IDs;
- file, namespace, class, struct, record, interface, enum, delegate, method,
  constructor, property, indexer, field, and parameter extraction;
- shared semantic nodes with separate physical declarations for partial types;
- overload-safe callable identities and semantic `declares` and `contains`
  relationships;
- bounded batch emission under one repository-aware C# index unit;
- privacy-safe Roslyn diagnostic codes and unresolved-error counts;
- package-derived plugin versions for automatic release invalidation and a
  conservative unresolved-symbol diagnostic classification that does not label
  syntax errors as unresolved relationships;
- direct multi-file, partial-type, overload, determinism, public-boundary, and
  full filesystem-to-store lifecycle tests.

Completed provider-neutral project-model slice:

- SDK-style project discovery from supplied `.csproj` content without requiring
  a local checkout or `MSBuildWorkspace`;
- deterministic source assignment supporting default compile items, explicit
  includes and removals, linked sources, and a loose-source fallback;
- project nodes, project-to-file containment, resolved project dependencies,
  common parse/compilation options, and dependency-ordered compilations;
- one atomic index unit per project, project-scoped symbol identities, and
  cleanup declarations for deleted projects and the superseded repository unit;
- bounded diagnostics for malformed projects, missing references, cycles, and
  compiler problems;
- multi-project, linked-source, compile-removal, dependency, and deleted-project
  regression coverage.

Next C# slice:

- inheritance and interface implementation using resolved Roslyn symbols.
- semantic calls and references where Roslyn resolves a unique target;
- import relationships and project/solution-model refinements proven necessary
  by real repositories. Full MSBuild evaluation remains an optional provider
  concern rather than a requirement of the core plugin.

Exit criteria:

- a multi-project fixture with partial types and overloads indexes correctly;
- cross-file callers, callees, references, inheritance, and implementations
  are queryable;
- syntax-only observations are never labeled semantic;
- repeat extraction produces byte-for-byte equivalent normalized facts.

## Milestone 5 — query service

Provide provider-neutral operations for:

```text
find symbol       find declarations
find references   find callers and callees
find implementations
find dependencies and dependents
get neighborhood  get bounded impact set
```

Queries include repository scope, exact matching rules, evidence filters,
maximum depth, maximum nodes, maximum edges, and deterministic ordering.
Provider-specific query access, if later needed, remains an explicitly advanced
API outside ordinary consumers.

Completed:

- exact qualified-name lookup with explicit ambiguity and deterministic order;
- declaration, reference, caller, callee, implementation, dependency,
  dependent, neighborhood, and impact-set operations;
- common depth, node, and edge bounds plus store-level evidence filtering;
- provider-neutral implementation over `ICodeGraphStore` without exposing a
  persistence query language.

## Milestone 6 — Ladybug provider

Implement `Penghou.Hetu.Ladybug` as a separate embedded persistence provider.
Ladybug-specific APIs and schema details remain internal to that package.

Implement:

- schema creation and compatibility versioning;
- transactional batch replacement;
- indexes for identity and common traversal directions;
- manifest, node contribution, declaration, edge, and evidence persistence;
- bounded query translation;
- health and schema compatibility checks.

Run the same conformance suite against memory and Ladybug. Benchmark ingestion,
repeat indexing, neighborhood traversal, and deletion on representative Penghou
repositories before committing to schema optimizations.

Completed durable-provider foundation:

- official LadybugDB 0.19.1 managed integration with host-selected native
  runtime packages;
- versioned schema creation and compatibility checks;
- transactional persistence of validated store mutations and restart replay;
- the complete `ICodeGraphStore` surface backed by the already-conformant
  provider-neutral materialization semantics;
- health reporting plus conformance and durable-reopen integration tests when
  the native engine is available.

Before calling the optimized Ladybug milestone complete:

- replace the initial durable command-state representation with normalized
  Ladybug node, relationship, declaration, manifest, and ownership tables;
- push bounded lookup and traversal into indexed Cypher queries;
- run the provider conformance suite in CI with every supported native runtime;
- benchmark ingestion, replacement, reopen, traversal, and deletion before
  selecting indexes or denormalized projections.

## Milestone 7 — incremental correctness and resilience

Cover:

- added, changed, moved, and deleted files;
- changed project references and compilation options;
- plugin-version invalidation;
- partial symbols spanning changed and unchanged files;
- failed or canceled extraction;
- corrupted or incompatible store metadata;
- bounded work on hostile repository layouts and dense graphs.

A clean extraction completion is not proof of graph integrity. Hetu validates
ownership, endpoints, identities, and batch invariants before commit.

Covered so far:

- added, changed, unchanged, and deleted source planning, including plugin
  version invalidation;
- project-option changes that alter compiler output and deleted projects that
  atomically remove their former units;
- partial-symbol survival across owner replacement and deletion;
- failed, cancelled, incomplete, foreign-owner, and late-terminal ingestion;
- source races, snapshot identity, corrupted size boundaries, hostile discovery
  limits, dense cyclic traversal, deterministic ordering, and bounded work;
- failed and cancelled runs retaining the latest successful incremental state.

Remaining resilience work follows the optimized Ladybug schema: incompatible
and corrupted durable metadata fixtures, interrupted native transactions, and
large-store replacement/traversal benchmarks.

## Milestone 8 — dogfood with Solo

Index Hetu and the other Penghou repositories, then integrate read-only code
comprehension into Solo.

Initial Solo uses:

- resolve relevant declarations for a planning task;
- include bounded callers, callees, implementations, and dependencies;
- estimate an impact neighborhood before editing;
- record the exact graph index version and query used for context selection;
- optionally persist selected graph observations or references in Cangjie.

Solo owns interpretation and context composition. Hetu returns attributable
graph facts and candidates; it does not decide architecture or invoke models.

Before leaving this milestone, review the vocabulary and plugin contract using
real C#, TypeScript, and Python examples. Do not start generator work until the
model survives that review.

## Later milestones

### Generated language plugins

Reserve `Penghou.Hetu.Generator` for a pipeline in which AI may help author or
adapt a grammar and declarative mapping, while conventional generation produces
a deterministic plugin. Runtime AI parsing and arbitrary generated runtime code
remain out of scope.

Potential packages include:

```text
Penghou.Hetu.TypeScript
Penghou.Hetu.Python
Penghou.Hetu.Java
Penghou.Hetu.Go
Penghou.Hetu.Rust
```

### Architectural analysis

Build deterministic primitives before higher-level interpretation:

- highly connected symbols;
- dependency neighborhoods;
- cross-project coupling;
- test relationships;
- component candidates;
- change-impact summaries.

Component, subsystem, and feature labels are not baked into the core graph
until their semantics and evidence are proven.

## Explicit non-goals for the first release

- a universal compiler or giant universal AST;
- exhaustive data-flow analysis;
- embeddings or vector search;
- runtime AI parsing or architectural labeling;
- runtime plugin generation or automatic package download;
- LSP, SCIP, Tree-sitter, or generated ANTLR integrations;
- distributed graph persistence;
- multi-language generated plugin packages.

## First-release acceptance criteria

1. Public abstractions contain no Roslyn, LadybugDB, ANTLR, LSP, SCIP, or AI
   dependency.
2. C# extraction uses repository/project-aware Roslyn semantic information.
3. Stable symbols and physical declarations are represented separately.
4. Every graph fact has extraction ownership and honest evidence.
5. Batch replacement is atomic, idempotent, and safe for partial symbols.
6. Memory and Ladybug stores pass the same conformance suite.
7. Repeat and incremental indexing do not create duplicate graph entities.
8. Bounded queries answer containment, callers, callees, implementations,
   references, dependencies, neighborhoods, and impact sets.
9. Diagnostics expose divergence and rejected facts without source content by
   default.
10. A future deterministic generated-language plugin can implement the public
    extraction contract without changing Hetu core.
