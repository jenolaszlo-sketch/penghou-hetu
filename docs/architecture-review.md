# Architecture & quality review - findings

Reviewed: 2026-08, current `main` (0.1.0-preview line).
**Ledger updated after remediation re-check:** 10 of the original 20 findings
are fixed; the remainder are itemized below. Resolved work is summarized once
and no longer tracked.

Scope: all five src packages (`Penghou.Hetu`, `Abstractions`, `CSharp`,
`Ladybug`, `Testing`) plus tests, CI, benchmarks, and ROADMAP.

## Resolved since review (do not re-track)

1. **Orphaned units from failed/cancelled runs** - the store now uses a
   staging model: replacements and deletions stage per-run, and a
   Failed/Cancelled transition discards the staged run entirely, so dead-run
   facts can never reach the published graph.
2. **Atomic delete+publish** - StageIndexUnitDeletionAsync stages deletions
   which CompleteIndexRunAsync applies together with publication.
3. **Unvalidated deletes** - staged deletions validate the running run and
   plugin membership like replacements do.
4. **Ladybug command-log reconstruction** - persistence normalized: running
   and terminal runs load separately, staged commands persist in their own
   HetuStage table, replay ordering is explicit.
5. **O(N^2) Ladybug replay** - happy-path writes no longer rebuild the
   in-memory store; full replay remains only on the rollback path.
6. **Per-query re-materialization** - materialized graphs cache per repository
   and invalidate on stage/complete.
7. **O(E*N) traversal** - outgoing/incoming adjacency indexes build during
   materialization.
8. **Redundant source-content copies** - right-sized capacity, buffer/length
   materialization without array copies, content shared across plugins
   claiming the same path. (Sessions decode to text by design.)
9. **Phantom obsolete unit** - the unconditional "csharp:repository" entry is
   removed.
10. **README HetuBuilder drift** - HetuBuilder exists (plugin/repository
    builders) and matches both quick-starts.

Also landed since the review: store contract split into ICodeGraphIndexStore
(staging/publish) and ICodeGraphReader (queries); provenance envelopes on
qualified-name, declaration, and traversal reads; CI format gate plus a
windows/linux/macos matrix; BENCHMARKS.md with a reproducible harness; runtime
public-API surface tests.
## Open findings

### A. Maintainability / OOP

1. **Package identity blur in namespaces** - CSharpCodeGraphPlugin,
   CSharpProjectDiscovery, and all Ladybug types still live in core's
   namespace Penghou.Hetu instead of Penghou.Hetu.CSharp /
   Penghou.Hetu.Ladybug. Consumers cannot tell which using directives pull
   Roslyn or LadybugDB transitive dependencies into scope.
2. **Composite string keys as convention** - plugin/path keys joined with a
   newline separator remain twice in CodeIndexingLifecycle; they rely on an
   implicit no-newline-in-paths invariant. Prefer one shared readonly record
   struct key type per concept.
3. **Hand-written RepositoryManifestConverter** - System.Text.Json handles
   positional records without it; inconsistent with the reflection path used
   for every other payload. Delete unless there is a versioning reason.

### B. Usefulness / semantics documentation

4. **C# semantic resolution uses host runtime assemblies** -
   CreatePlatformReferences reads TRUSTED_PLATFORM_ASSEMBLIES, so analyzing
   non-host target frameworks resolves against the wrong BCL, skewing
   diagnostics and unresolved-relationship counts. Document the limitation or
   adopt per-TFM reference assemblies.
5. **Query service convenience surface** - no symbol search by name pattern,
   no declarations-in-file, no batch multi-symbol lookup. Provenance envelopes
   landed; these consumer conveniences did not.
6. **GetImpactSetAsync is incoming-only** - reasonable definition, but document
   the decision or add an option including outgoing edges.

### C. Release engineering / project hygiene

7. **Single-TFM net10.0** - sibling packages target net8.0-net10.0; LTS
   consumers cannot take Hetu. State the decision in the README or add
   targets.
8. **CI coverage gaps** - format gate and OS matrix landed, but there is no
   coverage collection/threshold reporting and no .editorconfig. PublicApi
   snapshots exist for Abstractions and the runtime; CSharp/Ladybug/Testing
   have none.
9. **Benchmarks not CI-integrated** - harness is documented and reproducible;
   consider a scheduled or manual CI job so regressions surface.
10. **Pack publishes without package validation** - enable
    EnablePackageValidation before the first real release.

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

1. Small closes: namespaces (#1), converter removal (#3), impact-set decision
   note (#6), TFM decision note (#7).
2. Consumer value: query conveniences (#5) before Solo integration lands.
3. Hygiene: coverage thresholds + .editorconfig + remaining API snapshots
   (#8), package validation (#10), benchmark CI job (#9).
4. Deeper: shared key types (#2), platform-reference strategy (#4).