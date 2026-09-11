# Architecture & quality review - findings

Reviewed: 2026-08, current `main` (0.1.0-preview line).
**Ledger updated after remediation re-check:** 13 of the original 20 findings
are fixed; the remainder are itemized below. Resolved work is summarized once
and no longer tracked.

Branch update (2026-09, `feature/latticedb-provider`): `Ladybug` is renamed to
the durable `LatticeDb` provider, shared log folding moves into
`DurableCommandLog`, bounded name-pattern search lands with conformance
coverage, and a manual benchmarks workflow is added. References below use the
new `LatticeDb` name; behavior notes for the old `Ladybug` provider apply to
`LatticeDb` unless stated otherwise.

Scope: all five src packages (`Penghou.Hetu`, `Abstractions`, `CSharp`,
`LatticeDb`, `Testing`) plus tests, CI, benchmarks, and ROADMAP.

## Resolved since review (do not re-track)

1. **Orphaned units from failed/cancelled runs** - the store now uses a
   staging model: replacements and deletions stage per-run, and a
   Failed/Cancelled transition discards the staged run entirely, so dead-run
   facts can never reach the published graph.
2. **Atomic delete+publish** - StageIndexUnitDeletionAsync stages deletions
   which CompleteIndexRunAsync applies together with publication.
3. **Unvalidated deletes** - staged deletions validate the running run and
   plugin membership like replacements do.
4. **Durable command-log reconstruction** - persistence normalized: running
   and terminal runs load separately, staged commands persist in their own
   HetuStage table, replay ordering is explicit. Shared by `LatticeDb` through
   `DurableCommandLog` (extracted from the former `Ladybug` provider).
5. **O(N^2) durable replay** - happy-path writes no longer rebuild the
   in-memory store; full replay remains only on the rollback path.
6. **Per-query re-materialization** - materialized graphs cache per repository
   and invalidate on successful publication or durable restore.
7. **O(E*N) traversal** - outgoing/incoming adjacency indexes build during
   materialization.
8. **Redundant source-content copies** - right-sized capacity, buffer/length
   materialization without array copies, content shared across plugins
   claiming the same path. (Sessions decode to text by design.)
9. **Phantom obsolete unit** - the unconditional "csharp:repository" entry is
   removed.
10. **README HetuBuilder drift** - HetuBuilder exists (plugin/repository
    builders) and matches both quick-starts.
11. **Host-runtime reference limitation** - the README and ROADMAP decisions
    explicitly document that lightweight C# analysis uses
    TRUSTED_PLATFORM_ASSEMBLIES and may differ from a project's target BCL.
12. **Single-TFM decision** - .NET 10-only preview support is explicit in the
    README and decisions record, with concrete revisit conditions.
13. **Package validation** - packable projects enable NuGet package validation
    through Directory.Build.targets.

Also landed since the review: store contract split into ICodeGraphIndexStore
(staging/publish) and ICodeGraphReader (queries); provenance envelopes on
qualified-name, declaration, and traversal reads; CI format gate plus a
windows/linux/macos matrix; BENCHMARKS.md with a reproducible harness; runtime
public-API surface tests.

Also landed on `feature/latticedb-provider`: bounded name-pattern candidate
search (`FindNodesByNamePatternAsync` with conformance checks for substring,
case-insensitive, bounded deterministic, and empty results); manual
`.github/workflows/benchmarks.yml` (`workflow_dispatch` with short/default
jobs plus artifact upload); C# plugin split into partials without behavior
change.
## Open findings

### A. Maintainability / OOP

1. **Package identity blur in namespaces (decision recorded)** - Graph types
   stay in the common Penghou.Hetu namespace; only registration and options
   use Penghou.Hetu.CSharp / Penghou.Hetu.LatticeDb. No pre-stable rename:
   the churn is not worth it, and the choice is revisited only if API
   navigation proves painful during Solo dogfooding.
2. **Composite string keys as convention (resolved for plugin sources)** -
   planner, lifecycle, and index-state duplicate checks now share one
   `PluginSourceKey` readonly record struct; no separator invariant remains
   for that concept. `DurableCommandLog` slot keys stay strings by design:
   they fold staged and published commands with null-fallbacks, a different
   concept from plugin-source identity.
3. **Hand-written RepositoryManifestConverter (kept, reason recorded)** -
   retained for durable-log version tolerance (missing DisplayName/SourceUri,
   default RegisteredAt); see the comment on the converter in
   DurableCommandLog.cs.

### B. Usefulness / semantics documentation

4. **Query service candidate search** - declarations-in-file, project public
   surface, bounded multi-symbol lookup, multi-seed impact queries, and
   publication pinning have landed. Bounded name-pattern candidate search has
   landed on `feature/latticedb-provider` as an explicit substring primitive
   (ordinal case-insensitive, ordered by qualified name then node identity,
   explicit truncation); Solo still owns filters and ranking, so no fuzzy
   selection belongs in core.
5. **GetImpactSetAsync is incoming-only (documented)** - XML docs on both
   overloads now state the incoming-only definition and point to
   neighborhood/dependency traversals for outgoing edges.

### C. Release engineering / project hygiene

6. **CI coverage gaps (narrowed)** - format gate, OS matrix, minimal
   `.editorconfig`, and `ci.yml` XPlat Code Coverage collection with artifact
   upload have landed. PublicApi snapshots now exist for Abstractions, the
   runtime, Testing, CSharp, and LatticeDb. Remaining: coverage thresholds
   (no enforced floor yet).
7. **Benchmarks partially CI-integrated (verified)** - manual
   `.github/workflows/benchmarks.yml` (`workflow_dispatch`, short/default
   jobs) has landed and matches the documented `--job short` harness in
   BENCHMARKS.md; remaining work is scheduled runs and/or regression
   thresholds.

## Marang Gate 0.5 / Batch 4 handoff

The Gate 0.5 audit is recorded in the [roadmap handoff](../ROADMAP.md#marang-gate-05--batch-4-handoff).
The existing publication/index identities, typed symbol/node/declaration
identities, bounded query envelopes and traversals, contributor provenance,
source-drift detection, and staged durable recovery are reusable now. The
provider-neutral gaps are immutable historical snapshot/export references,
workspace/repository revision and freshness states, affected-test queries, and
an explicit same-repository concurrent-publication ordering policy.

Workflow/task/node ownership mapping and `SupervisorContextPackage` shaping are
deliberately Marang adapter concerns. Explicit path queries and changed-symbol
convenience methods are later enhancements, not Gate 0.5 blockers.

For resource and security review, per-source/total materialization and
per-query bounds do not impose a global retained-graph or replay cap. Query
envelopes can carry paths, names, properties, and documentation summaries, so
the Marang adapter must enforce total context budgets and redaction. Hetu's
graph store does not persist source blobs.

## Done well (preserve)

1. Conformance suite as executable contract for every durable store provider.
2. Evidence-kind honesty on every edge; syntax-only facts never claim
   compiler-resolved truth.
3. Staged-publication model making failed runs structurally incapable of
   polluting published graphs.
4. Scoped sinks make cross-repository/run/plugin emission impossible.
5. Privacy-safe diagnostics with failure-isolated callbacks; SHA-256 drift
   detection between planning and extraction with byte budgets.
6. Deterministic ordering everywhere; idempotent transitions with explicit
   rejection codes; provenance envelopes on queries.
7. The test suite reads like a specification: lifecycle laws, parallel bounds,
   cancellation, truncation, restart durability on the native engine, and
   ambiguity are all pinned.

## Suggested priority

1. Small closes: namespace decision (#1), converter review (#3), and impact-set
   direction note (#5).
2. Consumer value: #4 primitive has landed; validate filters and ranking needs
   during Solo integration rather than designing fuzzy selection speculatively.
3. Hygiene: coverage thresholds + .editorconfig + remaining API snapshots
   (#6), then scheduled benchmarks/regression thresholds (#7).
4. Deeper: shared key types (#2). Revisit exact target-framework reference
   resolution only when cross-target dogfooding demonstrates material errors.
