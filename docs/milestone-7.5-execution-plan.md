# Milestone 7.5 execution plan

Status: **complete in `0.2.0-preview.5`** — all five phases landed with
regression coverage. The one accepted gap is an extraction-throughput
benchmark baseline (store-operation baselines live in `BENCHMARKS.md`).

## Outcome

The C# plugin deterministically emits compiler-resolved `inherits`,
`implements`, `calls`, `references`, and `imports` relationships from the same
project compilations used for declaration extraction. It reports enough
coverage for consumers to distinguish a complete empty result from incomplete
semantic analysis.

## Working rules

- Roslyn performs C# binding; Hetu never resolves targets from names or text.
- Relationship targets normalize to existing Hetu identities. Constructed
  generics and reduced extension methods map to their source definitions.
- Only explicit source relationships are stored. Runtime dispatch, transitive
  closure, and heuristic targets stay outside this milestone.
- Project replacement remains the semantic invalidation boundary.
- Each phase adds graph assertions, coverage assertions, and deterministic
  repeat/incremental tests before the next phase begins.

## Phase 1 — semantic type and call correctness

- Resolve explicit base-list entries and classify interface inheritance as
  `inherits` and class/struct interface adoption as `implements`.
- Do not emit implicit base types or transitive interface closure.
- Add ordinary, static, extension, generic, interface-dispatched, constructor,
  implicit-object-creation, `this(...)`, and `base(...)` calls.
- Record ambiguous, unresolved, external, and unsupported call candidates
  without guessing graph targets.

Exit: focused tests cover cross-file and cross-project type relationships,
overloads, generics, constructors, and static-target virtual/interface calls.

## Phase 2 — reference ownership and evidence

- Resolve type, member, attribute, generic-argument, constraint, `nameof`, and
  `typeof` references.
- Select the smallest indexed owner: callable, property, field, type, then file.
- Skip declaration identifiers and unmodeled local symbols.
- Preserve deterministic evidence for repeated source/target occurrences.

Exit: each supported reference site has graph and evidence-location tests;
ambiguous, unresolved, and external targets have coverage tests.

## Phase 3 — imports and project-derived usings

- Model file-, namespace-, and compilation-scoped imports.
- Support namespace/type targets, aliases, `static`, and `global` metadata.
- Parse simple unconditional project `<Using>` items.
- Synthesize deterministic SDK implicit global usings for supported SDKs and
  mark them as project-derived evidence rather than physical files.
- Report unsupported or unevaluable project constructs as partial coverage.

Exit: ordinary, namespace-scoped, alias, static, global, project-item, and SDK
implicit-using cases have binding and coverage tests.

## Phase 4 — coverage contract

- Replace coarse emitted/unresolved totals with candidate, internal,
  cross-project, external, unresolved, ambiguous, unsupported, and emitted
  counts.
- Attribute coverage to its project index unit and derive
  not-applicable/complete/partial/unavailable state.
- Keep ordering, bounds, JSON transport, and public API snapshots deterministic.

Exit: consumers can explain why a relationship category is empty or partial
for every project index unit.

## Phase 5 — incremental, conformance, and performance proof

- Prove repeat indexing produces identical facts and coverage.
- Prove project replacement removes stale relationships, including binding
  changes in otherwise unchanged files.
- Add relationship-kind checks to the shared store conformance suite.
- Exercise a representative multi-project fixture within configured source and
  fact bounds; record a benchmark baseline and avoid solution-wide searches.

Exit: the roadmap acceptance criteria are covered on memory and LatticeDb
providers and the Milestone 7.5 roadmap entry can be marked complete.
