# Penghou.Hetu Roadmap

## Objective

Build a local-first, language-neutral code knowledge graph that gives Solo and
other .NET consumers deterministic repository comprehension without coupling
the graph model to Roslyn, LatticeDB, or any model provider.

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
- an in-memory graph store and a durable LatticeDB provider sharing a store
  conformance suite;
- a Roslyn C# plugin with provider-neutral SDK-style project discovery,
  declarations, symbols, containment, partial types, overload-safe identities,
  and project dependencies;
- exact symbol and declaration lookup plus bounded provider-neutral traversal
  and impact-query operations;
- run-scoped staging with atomic graph/state publication, failure cleanup, and
  durable restart semantics shared by memory and LatticeDb providers;
- publication-bound query envelopes with node, declaration, and edge
  contributors plus explicit traversal truncation diagnostics;
- deterministic publication receipts and source-state identities, fail-on-change
  publication query views, bounded batch symbol/impact queries, declarations by
  file, and project public-surface queries;
- a cohesive host facade with host-default indexing bounds, caller-supplied
  store ownership, and provider-neutral content-free readiness checks;
- separate indexing and reader contracts, cached in-memory materialization,
  adjacency indexes, and incremental LatticeDb mutation handling;
- native-provider CI, restart and corruption coverage, and recorded persistence
  benchmarks.
- LatticeDb replay distinguishes the latest completed publication from older
  completed run history. Reopening after multiple successful index runs restores
  every immutable run manifest while only the latest run restores the atomic
  repository index state; historical completion is never routed through the
  non-success `StoreIndexRunAsync` path.

This section is intentionally a summary rather than a historical delivery log.
The tests, public API snapshots, README, and Git history are authoritative for
already completed work.

## Marang Gate 0.5 / Batch 4 handoff

The current `0.2.0-preview.3` baseline is usable as a bounded, provider-neutral
graph source for Marang. This is an integration boundary and audit record; it
does not make Marang workflow semantics part of Hetu.

### Usable now in Hetu

- Stable repository, publication, and source-state identities, including the
  deterministic `CodeIndexIdentity`; symbol, node, and physical declaration
  identities are separate and stable (`CodeIndexIdentity.cs`,
  `CodeIdentities.cs`, `CodeIndexIdentityTests.cs`).
- Bounded exact lookup, declarations-in-file, public-surface, neighborhood,
  dependency/dependent, caller/callee, implementation, and incoming impact
  traversals. `CodeGraphQueryEnvelope<T>` binds results to the publication,
  applied query, and contributor provenance (`CodeGraphQueryService.cs`,
  `CodeGraphPublicationQuery.cs`).
- Contributor provenance through plugin/version/index-unit/source ownership,
  explicit ambiguity and traversal truncation, and source drift detection at
  the planning/extraction boundary (`CodeGraphFacts.cs`,
  `CodeIndexingLifecycle.cs`).
- Run-scoped staging, atomic publication, bounded plugin concurrency, and
  durable LatticeDB restart/recovery with corruption and interrupted-transaction
  coverage (`InMemoryCodeGraphStore.cs`, `LatticeCodeGraphStore.cs`, and their
  lifecycle/provider tests).

### Reusable upstream follow-ups

These should remain provider-neutral Hetu work, ordered as Gate 0.5 risks:

1. **P1 — Immutable historical graph snapshots or export references
   (landed on `feature/latticedb-provider`).** A publication-bound view fails
   when the latest publication moves, but Hetu retains only the latest graph.
   `CodePublicationSnapshot` now captures one publication as bounded,
   schema-versioned, SHA-256 integrity-checked units that re-import through
   the normal staging path, so a later query reproduces the original result.
   Serialized transport (index-in-CI, query-locally) remains Tier C
   follow-up work.
2. **P1 — Repository/workspace revisions and freshness semantics (freshness
   landed on `feature/latticedb-provider`).** `CodeIndexingService.CheckFreshnessAsync`
   compares live sources with the latest published state and reports
   unknown/fresh/stale/source-conflict with per-status counts, bound to the
   compared publication, without staging or publishing anything. Working
   revisions atop a pinned publication remain workspace-experiment work (see
   the [workspace experiment](docs/workspaces-design.md)).
3. **P1 — Affected-test query (landed on `feature/latticedb-provider`).**
   The C# plugin marks exact allowlisted test-framework methods with a
   `test-method` property (never inferred), and `GetAffectedTestsAsync`
   derives per-seed bounded test sets from incoming calls/references on both
   the service and publication-bound query surfaces.
4. **P1 — Same-repository concurrent publication ordering (landed on
   `feature/latticedb-provider`).** Mutations stay serialized, and completion
   is now optimistic: a run records the publication it planned against at
   registration, and completing after another publication lands fails
   explicitly instead of silently winning. Retries use a new run id against
   the latest publication. History replay restores without ordering checks
   and re-anchors resumed runs, so reopen never manufactures conflicts.

Explicit path/shortest-path queries and changed-symbol convenience methods can
remain later follow-ups; Marang can compose current traversals in the interim.

### Marang-owned adapter boundary

Workflow/task/node ownership mapping and `SupervisorContextPackage` shaping,
ranking, redaction, and final context budgets remain Marang responsibilities.
The adapter should use Hetu's publication, typed fact identities, locations,
evidence, query descriptor, and contributor provenance as its references rather
than adding workflow IDs to the Hetu graph model.

### Security and resource notes

Hetu bounds source enumeration/materialization, ingestion batches, and each
query, but there is no global retained-graph or LatticeDb replay cap. Marang must
apply a total context budget and treat large or highly connected results as
truncated. Query envelopes may expose repository-relative paths, symbol names,
properties, and documentation summaries; adapter output must apply its own
redaction and size policy. Source blobs are not persisted by the graph store.

## Hongxian optional code-memory boundary

Status: **design gate recorded; implementation deferred while Fuwen is the
active priority**

Hongxian sessions must remain fully usable without Hetu. The current source
dependency audit confirms that neither `Penghou.Hongxian` nor
`Penghou.Hongxian.Sqlite` has a Hetu, parser, graph-store, or Roslyn runtime
dependency. Its public-API analyzer is build-only with `PrivateAssets=all`;
future packed-consumer tests must prove that it does not flow transitively.

The responsibility split is fixed:

- Hongxian owns session continuity, immutable evidence, and opaque correlation.
- Hetu owns normalized code facts, language capabilities, publication identity,
  freshness, and code-graph queries.
- An optional integration adapter may correlate one immutable session resource
  revision with one exact Hetu publication. It must not copy graph nodes/edges
  into Hongxian or add session/workflow identities to Hetu's graph model.

Before Hetu multi-repository federation, structural publication deltas, Marang
MCP exposure, parser evaluation, or delegated-work synchronization through
Hetu:

- [ ] Prove an isolated Hongxian consumer has no transitive Hetu, Roslyn, ANTLR,
  or LatticeDB dependencies.
- [ ] Decide the optional integration package's repository ownership without
  changing dependency direction. `Penghou.Hongxian.Hetu` may depend on both
  sides; Hongxian core/storage and Hetu core must not depend on it.
- [ ] Define exact revision/publication correlation, idempotent synchronization,
  freshness, concurrent publication ordering, superseding revisions, late
  completion, and forward reconciliation after partial failure.
- [ ] Keep zero adapters valid. Capability absence is discovered explicitly;
  it is not represented by a mandatory no-op provider or an adapter-generated
  `NotConfigured` result.
- [ ] Preserve parser-private implementations, normalized facts, replaceable
  graph persistence, domain-specific bounded queries, deterministic language
  registration, and explicit installed-language/provider/store capabilities.
- [ ] Add an enabled-path acceptance test covering R1 -> H1, R2 -> H2, exact
  publication evidence, synchronization failure without session rollback, and
  removal of the optional adapter without loss of generic session behavior.

The integration host owns resource mapping, credentials, filesystem/network
authority, bounds, and redaction. Opaque session metadata is correlation data,
not authority to open a repository or activate a provider. No generic graph,
universal parser, dynamic plugin loader, MEF composition, or MCP surface belongs
in this boundary milestone.

## Post-boundary proving sequence — shared workspaces and structural catch-up

Status: **planned after Fuwen is usable and the Hongxian optional-capability
boundary is proven**

The product hypothesis is that Hetu can reduce the supervisor's catch-up cost
after delegated coding work. Validate that hypothesis in Marang before growing
Hetu into a broad federation platform.

### Gate A — reproducible repository publications

The current store retains historical run manifests but restores only the latest
graph. A workspace manifest containing old publication IDs is therefore not a
reproducible structural snapshot by itself.

- [ ] Complete the existing immutable historical graph snapshot/export-reference
  work before claiming reproducible workspace publications or deltas.
- [ ] Bind each retained graph state to its repository identity, exact source
  state, plugin/version set, fact vocabulary/schema, and integrity metadata.
- [ ] Define retention failure explicitly: a workspace publication whose member
  graph is unavailable is identifiable but not queryable and must never fall
  through to the repository's latest graph.

### Gate B — immutable multi-repository composition

- [ ] Add stable workspace identity and a canonical immutable publication
  manifest containing an ordered mapping from repository identity to exact
  repository publication. Do not flatten repositories into one anonymous graph.
- [ ] Make creation idempotent and content-addressed, reject duplicate repository
  identities and mismatched source state, and preserve predecessor/lineage
  without using wall-clock order as authority.
- [ ] Keep composition separate from federation. The first workspace publication
  may provide deterministic membership and per-repository query routing without
  claiming cross-repository symbol resolution.
- [ ] Add explicit branch/candidate lineage. Parallel workers produce separate
  candidate publications; an integration or promotion step creates a new
  publication. Do not pretend independently edited branches automatically form
  one later workspace state.

### Gate C — bounded structural change

- [ ] Define a raw `GraphChangeSet` over two available immutable publications:
  added/removed/stably identified changed facts and edges, with contributor and
  evidence changes preserved. Define “changed” precisely rather than comparing
  display names or mutable locations.
- [ ] Keep factual graph delta separate from derived impact analysis. Impacted
  nodes/repositories require their own query descriptor, bounds, truncation,
  evidence, and algorithm/version identity.
- [ ] Bound or page every delta dimension and provide deterministic summaries so
  a large rename or regenerated project cannot exhaust MCP or model context.
- [ ] Distinguish unavailable history, incompatible schema/plugin semantics,
  ambiguous identity, and source drift from a valid empty change set.

### Gate D — Marang/Codex evaluation

- [ ] Expose only a minimal, authorization-scoped, bounded code-query and
  structural-catch-up surface through Marang. Hetu does not acquire an MCP host.
- [ ] Compare paired tasks pinned to the same source/publication state, recording
  source reads, searches, relevant relationships found/missed, false facts,
  elapsed time, and observable token use. Treat token measurements as noisy.
- [ ] Use independently reviewable expected facts or labeled fixtures for parser
  precision/recall. Codex may discover discrepancies but is not the correctness
  oracle; confirmed defects become regression tests.
- [ ] Stop or narrow the feature if structural catch-up does not materially
  reduce source inspection or if false relationships increase review risk.

Cross-repository resolution follows composition only when a concrete query needs
it. Any relationship across repositories requires evidence such as an explicit
project/package/workspace mapping and exact version provenance; a package
version must not silently resolve to an unrelated working-tree checkout.

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

- **Name-pattern candidate search (M)** — bounded prefix/pattern candidates
  with explicit truncation; Solo ranks results.

### Tier C — strategic

- **Publication snapshot export/import (M, object form landed)** —
  store-agnostic, bounded, schema-versioned serialized publications with
  integrity hashes and explicit compatibility rules; enables index-in-CI,
  query-locally workflows and makes LatticeDb optional for read-only
  consumers. The in-process snapshot/import path exists; serialized transport
  is the remaining piece.
- **Test-to-production mapping (M)** — detect test projects and emit exercised
  -by relationships once semantic calls land, so impact sets include the tests
  to run.
- **Entry-point/route heuristics (M)** — `Main`, controller actions,
  minimal-API endpoints as heuristic-evidence nodes; must respect the
  honest-evidence law.
- **TODO/FIXME comment nodes (S)** — linked to their containing symbol for
  graph-addressable cleanup tracking.

### Explicitly not now

Textual query language, SCIP/LSIF export, and embeddings or vector search remain
out of scope. Multi-repository composition and narrowly evidenced federation
follow only the post-boundary proving sequence above; do not fold them into the
semantic-relationship milestone.

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
- a cohesive host facade for indexing and querying, with typed C# and LatticeDb
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

1. Public abstractions contain no Roslyn, LatticeDB, parser, or AI dependency.
2. Stable symbols and physical declarations remain separate.
3. Every graph fact has extraction ownership and honest evidence.
4. A successful index publication atomically updates graph facts, run status,
   source state, and query-visible provenance.
5. Failed and cancelled runs cannot alter the last successful graph snapshot.
6. Memory and LatticeDb pass the same conformance suite.
7. C# extraction provides useful semantic calls, references, inheritance, and
   implementation relationships without guessed targets.
8. Queries are deterministic, bounded, attributable, and explicit about
   ambiguity, index coverage, and truncation.
9. Repeat and incremental indexing do not create duplicate graph entities.
10. Solo can assemble explainable, snapshot-validated code context without
    depending on Roslyn or LatticeDB types.
11. A future deterministic non-C# plugin can implement the public extraction
    contract without changing Hetu core.
12. Documentation quick-start samples compile against the public packages.

## Durable store schema evolution

`LatticeCodeGraphStore.CurrentSchemaVersion` is validated on open and a
mismatch is rejected rather than migrated. There are no in-place upgrades in
preview: a schema change ships with a schema-versioned export/import path
(store-agnostic, bounded, integrity-hashed, per Tier C publication snapshot
export/import) so hosts re-index or import instead of running mixed-version
binaries against one file. The health check reports the on-disk version to
make the mismatch actionable before any write is attempted.
