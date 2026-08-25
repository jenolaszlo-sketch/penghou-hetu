# Architecture & quality review - findings

> Update, 2026-08-26: findings 1-7, 12, 14, and 20 are resolved. Finding 16
> now has a format gate and runtime public-surface snapshot; multi-targeting,
> coverage policy, and broader package snapshots remain. Finding 19 is partly
> stale because `BENCHMARKS.md` documents the current workflow, although
> benchmarks are intentionally not a blocking CI gate. The active findings are
> 8-11, 13, 15, 17-18, and the remaining portions of 16/19. Historical detail
> is retained below until the next consolidated review.

Reviewed: 2026-08, current `main` (0.1.0-preview.1 scaffolding, pre-first
release). Read-only audit; no code changes accompany this document.

Scope: all five src packages (`Penghou.Hetu`, `Abstractions`, `CSharp`,
`Ladybug`, `Testing`) plus tests, CI, benchmarks, and ROADMAP.

## Summary

The contract design is excellent: parser-neutral vocabulary, evidence-honest
edges, bounded everything, atomic index-unit replacement, a conformance suite
every durable store must pass, and unusually disciplined tests (determinism,
privacy, cancellation, parallel bounds - 80 facts/theories across three test
projects). The dominant opportunities are two durability/correctness gaps
around failed runs, an O(N^2) persistence pattern in the Ladybug provider,
per-query graph re-materialization in the in-memory store, and README drift
(the primary quick-start documents an API that does not exist).

## A - Correctness & durability

### 1. Failed/cancelled runs orphan partially-ingested units permanently

`InMemoryCodeGraphStore._units` keeps every replacement ever written.
`EnsureKnownOwnership` gates *new* writes on run status, but nothing purges
units when their run transitions to Failed/Cancelled (`ValidateRunTransition`
permits the transition; the lifecycle only deletes reported obsolete units on
*success*). Orphaned units keep materializing into every query forever, so
stale facts from dead runs pollute results.

Opportunity: purge units by `(repositoryId, runId)` on terminal run
transition, or make materialization skip units whose owning run is not a
completed-and-published run.

### 2. Obsolete-unit deletion is not atomic with state publication

`CodeIndexingLifecycle.IndexAsync` deletes obsolete units and *then* calls
`CompleteIndexRunAsync`. A crash between the two leaves the last-published
source manifest referencing units that no longer exist (manifest/graph
divergence until the next successful run).

Opportunity: fold deletions into `CompleteIndexRunAsync` or expose a
store-level transaction spanning delete + publish.

### 3. DeleteIndexUnitAsync performs no ownership validation

Unlike `ReplaceIndexUnitAsync`'s strict repository/run/plugin checks, any
caller can delete any unit of any run in any state.

Opportunity: validate repository existence at minimum; document why deletion
is intentionally looser than replacement if it stays that way.

### 4. Ladybug LoadCommands reconstruction is fragile

Running runs are re-emitted as running-form commands while failed/cancelled
runs also appear again in `terminals`, so `_commands` temporarily holds two
entries per terminal run until the next mutation dedups via `SameSlot`.
Replay correctness depends on phase ordering inside `ReplayAsync`.

Opportunity: persist one canonical append-only command log with explicit
tombstones for deletes instead of reconstructing from table contents.

## B - Performance & scalability

### 5. Ladybug replays the entire command log on every mutation

`MutateAsync` applies the command incrementally to `_inner`, persists, then
rebuilds a whole new `InMemoryCodeGraphStore` via `ReplayAsync(next)`.
Indexing N units costs O(N^2) replay work - the dominant scalability ceiling.
The post-persist replay is redundant belt-and-braces: the transaction plus
the rollback path already restore consistency.

Opportunity: trust the transactional apply; drop the full replay from the
write path.

### 6. In-memory store re-materializes the whole repository graph on every read

`Materialize(repositoryId)` runs inside `GetNodeAsync`, `FindSymbolAsync`,
qualified-name lookup, declarations lookup, and traversal - all under the
global lock. Cost is O(units x facts) per query.

Opportunity: cache the materialized graph per repository; invalidate on
replace/delete/complete.

### 7. Traversal scans all edges per dequeued node

`Traverse` filters the full edge set for every dequeued node - O(E*N) per
traversal.

Opportunity: build an adjacency index during materialization (the Ladybug
provider already maintains exactly this via its `HetuAdjacency` table).

### 8. Source content is copied three-plus times through the pipeline

The planner hash pass reads bytes; `MaterializeSourcesAsync` re-reads into a
`MemoryStream` and then calls `ToArray()` (a second copy per file); the C#
session reads yet again into strings. Additionally the same source path is
duplicated once per claiming plugin.

Opportunity: right-sized buffers, shared immutable content across plugins
claiming the same path, and `ReadOnlyMemory<byte>` end-to-end where practical.

## C - Maintainability / OOP

### 9. Package identity blur in namespaces

`CSharpCodeGraphPlugin`, `CSharpProjectDiscovery`, and all Ladybug types live
in core's namespace `Penghou.Hetu` rather than `Penghou.Hetu.CSharp` /
`Penghou.Hetu.Ladybug`. Consumers cannot tell which using directives pull
Roslyn or LadybugDB transitive dependencies into scope.

Opportunity: align namespaces with package names.

### 10. Composite string keys as convention

Plugin/path composite keys built with newline separators (twice in the
lifecycle), private `OwnerKey` records duplicated in sink and store, private
`RunKey` - all rely on an implicit "no newline in paths" invariant.

Opportunity: one shared `readonly record struct` key type per concept.

### 11. C# semantic resolution uses host runtime assemblies

`CreatePlatformReferences` reads `TRUSTED_PLATFORM_ASSEMBLIES`, so analyzing
a net48/netstandard project resolves symbols against the host (net10) BCL,
skewing diagnostics and `unresolvedRelationships` counts.

Opportunity: document the limitation prominently or adopt per-TFM reference
assemblies.

### 12. Vestigial obsolete unit reported every run

`Session.ExtractAsync` unconditionally appends `"csharp:repository"` to
obsolete units - nothing ever creates that id (real units are
`csharp:project:{hash}`, including the loose project). Every successful run
reports a phantom deletion.

Opportunity: remove it.

### 13. Hand-written RepositoryManifestConverter is unnecessary

System.Text.Json serializes positional records without a custom converter;
the manual converter duplicates serialization logic and is inconsistent with
the reflection-based path used for every other payload.

Opportunity: delete it unless there is a deliberate versioning reason (if so,
document it).

## D - Usability / docs drift

### 14. README documents an API that does not exist

Both quick-starts use `HetuBuilder().AddRepositoryProvider(...)` /
`AddPlugin(...)` / `BuildRepositoryProviderRegistry()` - no such type exists
anywhere in src or tests. This is the primary onboarding path and it cannot
compile.

Opportunity: either build the small builder or fix the samples to construct
the registries directly.

### 15. Single-TFM net10.0 while sibling packages target net8.0-net10.0

Deliberate preview choice perhaps - but LTS consumers cannot take Hetu at
all, and nothing in the README states the decision.

Opportunity: state the decision, or add net8.0/net9.0 targets.

### 16. CI gaps vs sibling repos

Single-TFM build/test, no format gate, no coverage collection or thresholds,
and no `.editorconfig`. Core has a useful type-snapshot test
(`PublicApiContractTests`) but only for Abstractions - the other four packages
have no surface snapshot.

## E - Usefulness

### 17. Query service is thin where consumers will need convenience

No symbol search by name pattern, no "declarations in file", no batch lookup
of many symbols in one call. Solo's context-selection use case will want
these soon; all are cheap additions over existing store primitives.

### 18. GetImpactSetAsync is incoming-only

Reasonable definition, but changing a shared base type can break dependents
through outgoing inheritance edges to implementations elsewhere. Worth a
documented decision or an option to include outgoing edges.

## F - Release engineering

### 19. Benchmarks exist but are not wired up

`benchmarks/Program.cs` is present but neither CI nor BENCHMARKS.md shows a
current verified workflow against today's APIs.

### 20. Pack publishes without package validation

`EnablePackageValidation` / baseline comparison is cheap to enable now,
before the first real release.

## Done well (preserve)

1. Conformance suite as executable contract for every durable store provider.
2. Evidence-kind honesty on every edge; syntax-only facts never claim
   compiler-resolved truth.
3. Scoped sinks make cross-repository/run/plugin emission structurally
   impossible.
4. Privacy-safe diagnostics with failure-isolated callbacks.
5. SHA-256 drift detection between planning and extraction, with explicit
   per-source and total byte budgets.
6. Deterministic ordering everywhere; idempotent store transitions with
   explicit rejection codes.
7. The test suite reads like a specification: lifecycle laws, parallel
   bounds, cancellation, truncation, and ambiguity are all pinned.

## Suggested priority

1. Durability trio: #1 orphaned units, #2 atomic delete+publish,
   #3 delete validation.
2. Scale: #5 drop post-persist replay, #6/#7 materialization cache +
   adjacency index.
3. Trust: #14 fix-or-build HetuBuilder, #9 namespaces, #12 phantom obsolete
   unit, #11 TFM resolution note.
4. Release hygiene: #16 CI parity (multi-TFM, format gate), #20 package
   validation.
5. Everything else opportunistically.
