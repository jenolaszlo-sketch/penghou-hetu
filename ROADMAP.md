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

## Next C# milestone — useful semantic relationships

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

Exit criteria:

- cross-file and cross-project callers, callees, references, inheritance, and
  implementations are queryable;
- partial types, overloads, generics, extension methods, and interface dispatch
  have explicit regression coverage;
- unresolved or ambiguous targets never create guessed edges;
- repeat and incremental extraction remain deterministic;
- coverage/capability metadata accurately describes the published index.

## Milestone 8 — dogfood with Solo

This milestone will be designed and implemented together with Solo rather than
completed speculatively.

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

### Generated language plugins

Reserve `Penghou.Hetu.Generator` for deterministic plugin generation from
grammars and declarative mappings. AI may help author those inputs, but runtime
AI parsing and arbitrary generated runtime code remain out of scope.

Potential future packages include TypeScript, Python, Java, Go, and Rust
plugins. Their design must validate the shared vocabulary rather than copy C#
semantics mechanically.

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
