# Penghou.Hetu Roadmap

## Objective

Build a local-first, language-neutral code knowledge graph that gives Solo and
other .NET consumers deterministic repository comprehension without coupling
the graph model to Roslyn, LadybugDB, or any model provider.

The central invariant is:

> Hetu defines what code means in the graph. Plugins define how those facts are
> discovered.

## Architectural laws

- Extraction is repository-aware and session-oriented. Plugins choose their
  atomic index units rather than inheriting file ownership from the planner.
- Semantic symbols and physical declarations remain separate.
- Repository, snapshot, index-run, plugin, plugin-version, and index-unit
  identities remain distinct.
- Every fact has an owner. Non-trivial relationships carry honest evidence.
- Successful graph publication is atomic, idempotent, and consistent with the
  published source state.
- Persistence and language extraction remain replaceable behind
  provider-neutral contracts.
- Queries are bounded, deterministic, provenance-aware, and explicit about
  ambiguity and truncation.
- Hetu returns attributable facts and candidates. Solo owns interpretation,
  ranking, and context composition.

## Decisions

Recorded choices with the conditions that would trigger revisiting them:

- **Single target framework net10.0.** Sibling packages multi-target
  net8.0-net10.0; revisit when the first external consumer on an LTS
  framework needs Hetu, or at general availability.
- **Lightweight csproj parsing without MSBuild evaluation.** Conditions,
  imports, custom targets, and solution configurations are not interpreted.
  Revisit only as an opt-in provider concern when a real repository fails to
  index correctly because of it.
- **Synthetic `@loose/csharp` project** for sources outside any discovered
  project. Revisit if loose sources prove ambiguous in practice.
- **C# semantic resolution references the host runtime**
  (`TRUSTED_PLATFORM_ASSEMBLIES`), so non-host target frameworks resolve
  against a different BCL. Revisit if unresolved-relationship counts prove
  misleading on cross-target repositories; until then this limitation must be
  documented wherever extraction semantics are.

## Current baseline

Milestones 1 through 7 established the working foundation:

- portable graph identities, vocabulary, properties, evidence, locations,
  extraction sessions, batches, and validation;
- deterministic repository discovery, source hashing, incremental planning,
  bounded indexing, diagnostics, and plugin registration;
- an in-memory graph store and a durable LadybugDB provider sharing a store
  conformance suite;
- a Roslyn C# plugin with provider-neutral SDK-style project discovery,
  declarations, symbols, containment, partial types, overload-safe identities,
  and project dependencies;
- exact symbol and declaration lookup plus bounded provider-neutral traversal
  and impact-query operations;
- run-scoped staging with atomic graph/state publication, failure cleanup, and
  durable restart semantics shared by memory and Ladybug providers;
- publication-bound query envelopes with node, declaration, and edge
  contributors plus explicit traversal truncation diagnostics;
- separate indexing and reader contracts, cached in-memory materialization,
  adjacency indexes, and incremental Ladybug mutation handling;
- native-provider CI, restart and corruption coverage, and recorded persistence
  benchmarks.

This section is intentionally a summary rather than a historical delivery log.
The tests, public API snapshots, README, and Git history are authoritative for
already completed work.

## Milestone 7.5 — useful semantic relationships

The query surface exists, but the C# plugin must emit the relationships that
make comprehension and impact analysis useful.

Implement, using uniquely resolved Roslyn symbols:

- inheritance and interface implementation;
- semantic calls and references;
- import relationships;
- return and accepted-parameter relationships where the cross-language meaning
  is sufficiently precise;
- stable diagnostics for unresolved, ambiguous, unsupported, or deliberately
  omitted relationships.

Do not guess targets. Syntax-only observations must not be labeled semantic.
Full MSBuild evaluation remains an optional future provider concern rather than
a requirement of the lightweight C# plugin.

Add index-coverage metadata so a consumer can distinguish “no relationships
exist” from “this plugin/index did not produce that relationship kind.”

Tier-A feature candidates below intentionally ride along with this extraction
pass: they decorate symbols the plugin already emits.

Exit criteria:

- cross-file and cross-project callers, callees, references, inheritance, and
  implementations are queryable;
- partial types, overloads, generics, extension methods, and interface dispatch
  have explicit regression coverage;
- unresolved or ambiguous targets never create guessed edges;
- repeat and incremental extraction remain deterministic;
- coverage/capability metadata accurately describes the published index;
- the store conformance suite gains relationship-kind checks alongside plugin
  tests;
- indexing a repository of Roslyn-solution size completes within the configured
  per-source and total byte budgets.

## Feature candidates

Candidates are promoted into a numbered milestone when scheduled. None of them
changes the architectural laws. Effort: S (days), M (weeks), L (longer).

### Tier A — ride along with Milestone 7.5

- **Documentation-comment extraction (S)** — attach `<summary>`/`remarks`
  text to symbol nodes as bounded syntax-evidence properties; declaration plus
  its documented intent in one node. Normalize deterministically and cap both
  per-symbol text and total extracted documentation.
- **Modifier/attribute properties (S)** — `static/virtual/abstract/sealed`,
  access level, and an explicit allowlist such as `[Obsolete]`, test-framework,
  and route attributes; unlocks public-surface, obsolete-member, and
  test-filtering queries without turning arbitrary attribute payloads into an
  unbounded property channel.
- **Literal values for enums/constants (S)** — lets consumers answer
  configuration questions without reading source.
- **Package-reference nodes (M)** — `PackageReference` items become bounded
  syntax-evidence external dependency nodes with version and unexpanded
  condition metadata; do not claim evaluated MSBuild semantics.
- **Solution-file scoping (M)** — parse `.sln` for canonical project sets,
  configurations, and explicit solution dependencies instead of directory-walk
  inference. Project-reference edges remain the primary build-order evidence.

### Tier B — query surface

- **Batch symbol resolution (S)** — resolve many paths/qualified names in one
  bounded call for Solo context assembly.
- **Declarations-in-file and project public surface (S)** — thin query-service
  methods over existing containment edges.
- **Name-pattern candidate search (M)** — bounded prefix/pattern candidates
  with explicit truncation; Solo ranks results.

### Tier C — strategic

- **Publication snapshot export/import (M)** — store-agnostic, bounded,
  schema-versioned serialized publications with integrity hashes and explicit
  compatibility rules; enables index-in-CI, query-locally workflows and makes
  Ladybug optional for read-only consumers.
- **Test-to-production mapping (M)** — detect test projects and emit exercised
  -by relationships once semantic calls land, so impact sets include the tests
  to run.
- **Entry-point/route heuristics (M)** — `Main`, controller actions,
  minimal-API endpoints as heuristic-evidence nodes; must respect the
  honest-evidence law.
- **TODO/FIXME comment nodes (S)** — linked to their containing symbol for
  graph-addressable cleanup tracking.

### Explicitly not now

Textual query language, SCIP/LSIF export, embeddings or vector search, and
multi-repository federation remain out of scope; revisit only on demonstrated
ecosystem demand.

## Milestone 8 — dogfood with Solo

This milestone begins once Milestone 7.5 satisfies its exit criteria. It will
be designed and implemented together with Solo rather than completed
speculatively.

Index Hetu and the other Penghou repositories, then integrate read-only code
comprehension into Solo for:

- resolving relevant declarations for a planning task;
- selecting bounded callers, callees, implementations, and dependencies;
- estimating an impact neighborhood before editing;
- recording the exact graph publication and query used for context selection;
- optionally persisting selected graph observations or references in Cangjie.

During dogfooding, evaluate these usability additions from real workflows:

- deterministic symbol discovery scoped by language, node kind, project, or
  containing symbol;
- canonical symbol-key lookup and source-location-to-enclosing-symbol lookup;
- declarations and symbols by file or project;
- bounded, hash-validated source excerpts with snapshot mismatch detection;
- a cohesive host facade for indexing and querying, with typed C# and Ladybug
  registration helpers.

### Transactional workspace experiment

Evaluate Hetu as a deterministic model of code Solo is actively constructing,
not only as an index of the last committed repository. Keep this experimental
until the read-only integration proves which semantics are genuinely useful.

Start with the smallest vertical slice:

```text
published repository graph
        -> begin workspace pinned to one publication and validated source view
        -> replace one existing C# source in memory
        -> refresh the affected project graph
        -> run bounded provenance-aware queries
        -> discard the workspace
```

The workspace view must be implemented as a repository overlay. Added and
modified sources shadow the base repository, deleted sources disappear, and
unchanged sources continue to come from the pinned base snapshot. Language
plugins consume the resulting `ICodeRepositorySource` view without learning
whether content came from disk, a VFS, or a workspace edit. Do not create
temporary project files or introduce filesystem assumptions.

Pinning a graph publication is insufficient when its repository provider is a
live filesystem. Beginning and refreshing a workspace must also validate the
base source manifests and hashes, or open a provider-defined immutable source
snapshot. A mismatch fails explicitly rather than combining facts from one
publication with later source bytes.

If the first slice proves useful, extend it in the staged order documented in
[docs/workspaces-design.md](docs/workspaces-design.md), which also holds the
revision record, graph-diff semantics, and the workspace/source-persistence
separation. Summary of that design: revisions are immutable and append-only
with a movable logical head; rollback moves the head rather than reversing
graph mutations; CodeGraphDiff is a first-class derived result between
publications or revisions; source blobs stay out of `ICodeGraphStore` behind a
future `ICodeWorkspaceStore` whose recovered workloads revalidate their pinned
base publication.

`BasePublicationId` refers to Hetu's existing successfully published
`CodeIndexRunId`; it does not introduce a parallel repository-publication
identity. A workspace revision uses its own typed `WorkspaceRevisionId`, which
also identifies the atomically refreshed working graph for that revision.

The working graph must remain separate from the repository's last successful
published graph. Refresh should atomically publish a workspace revision or
leave its prior working graph intact. Partially edited or temporarily invalid
code is acceptable: preserve valid structural facts, report compiler
diagnostics and unresolved relationships, and never promote guessed semantic
edges.

Important boundaries:

- beginning or refreshing a workspace never mutates the published repository
  graph;
- every operation is bounded and cancellation-aware;
- diagnostics remain source-content-free by default;
- disposal, expiry, and abandoned-workspace cleanup are host-triggerable;
- graph diff and rollback operate on immutable revisions;
- Git integration and applying edits to the real repository remain host/Solo
  responsibilities;
- incremental Roslyn compilation reuse is an optimization behind an optional
  capability, not a requirement imposed on every language plugin.

Workspace experiment exit criteria:

- a modified source can be queried semantically without changing disk or the
  published graph;
- discard restores the exact published view with no cleanup ambiguity;
- add/delete and incomplete-code behavior are deterministic and tested;
- workspace query envelopes identify both the base publication and workspace
  revision;
- measurements justify whether checkpoints, persistence, graph diff, and
  incremental plugin sessions should become supported product contracts.

Avoid fuzzy symbol selection in Hetu core. Return explicit candidates and let
Solo rank them.

Before leaving this milestone, review the vocabulary and plugin contract using
real C#, TypeScript, and Python examples. Do not start generator work until the
model survives that review.

## Later opportunities

### Deterministic architectural analysis

Build primitives proven useful during Solo integration:

- highly connected symbols and bounded dependency neighborhoods;
- cross-project coupling and test relationships;
- component candidates and change-impact summaries.

Component, subsystem, and feature labels do not belong in core until their
semantics and evidence requirements are proven.

Workspace graph comparison is a likely consumer of these primitives, but
architectural scoring remains derived analysis rather than workspace mutation
logic. Candidate comparisons must report their evidence and bounds rather than
claiming one implementation is universally better.

### Generated language plugins

Reserve `Penghou.Hetu.Generator` for deterministic plugin generation from
grammars and declarative mappings. AI may help author those inputs, but runtime
AI parsing and arbitrary generated runtime code remain out of scope.

Potential future packages include TypeScript, Python, Java, Go, and Rust
plugins. Their design must validate the shared vocabulary rather than copy C#
semantics mechanically.

## Engineering health

Tracked separately in [docs/architecture-review.md](docs/architecture-review.md),
which carries the live open-findings ledger (namespace/package alignment,
composite key types, coverage gates, API-surface snapshots for every package,
package validation, benchmark CI integration). Items there that gate the first
release: package validation, per-package public-API snapshots, and coverage
reporting with thresholds. Keep that ledger and this roadmap in sync — the
roadmap owns features; the review owns engineering debt.

## Explicit non-goals for the first release

- a universal compiler or giant universal AST;
- exhaustive data-flow analysis;
- embeddings or vector search;
- runtime AI parsing, architectural labeling, or plugin generation;
- automatic plugin or NuGet downloading;
- distributed graph persistence;
- provider query languages in ordinary consumer APIs.

## First-release acceptance criteria

1. Public abstractions contain no Roslyn, LadybugDB, parser, or AI dependency.
2. Stable symbols and physical declarations remain separate.
3. Every graph fact has extraction ownership and honest evidence.
4. A successful index publication atomically updates graph facts, run status,
   source state, and query-visible provenance.
5. Failed and cancelled runs cannot alter the last successful graph snapshot.
6. Memory and Ladybug pass the same conformance suite.
7. C# extraction provides useful semantic calls, references, inheritance, and
   implementation relationships without guessed targets.
8. Queries are deterministic, bounded, attributable, and explicit about
   ambiguity, index coverage, and truncation.
9. Repeat and incremental indexing do not create duplicate graph entities.
10. Solo can assemble explainable, snapshot-validated code context without
    depending on Roslyn or LadybugDB types.
11. A future deterministic non-C# plugin can implement the public extraction
    contract without changing Hetu core.
12. Documentation quick-start samples compile against the public packages.
