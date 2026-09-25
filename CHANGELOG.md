# Changelog

Notable changes to Penghou.Hetu are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise public contracts.

## Unreleased

## 0.2.0-preview.5

Milestone 7.5 (useful semantic relationships) is complete:

- C# semantic relationships with honest evidence: explicit base-type
  inheritance and interface implementation, ordinary/implicit/constructor
  calls, smallest-owner references, attributes, and scoped imports with
  alias, `static`, and `global` metadata. No transitive closure, no guessed
  targets.
- Coverage contract: per-kind candidate, internal, cross-project, external,
  ambiguous, unsupported, and emitted counts, plus per-index-unit entries, so
  consumers can distinguish an empty result from an incomplete index.
- Store conformance relationship-kind checks alongside plugin tests; repeat
  and incremental determinism including coverage; stale-relationship removal
  on rebind.
- Tier-A ride-alongs: `<summary>`/`<remarks>` doc properties, modifier and
  attribute properties with test-framework and route allowlists, constant
  literals, package-reference nodes, and solution scoping with diagnostics
  for unevaluated solution constructs.
- Coverage thresholds enforced in CI; LatticeDB benchmark baseline refreshed
  for LatticeDbSharp 0.2.0.

## 0.2.0-preview.1 – 0.2.0-preview.3

Foundation: portable graph identities, vocabulary, properties, evidence, and
locations; extraction sessions, batches, and validation; deterministic
repository discovery, hashing, incremental planning, and bounded indexing;
in-memory and LatticeDB providers behind a shared conformance suite; Roslyn
C# declarations, symbols, containment, and project discovery; exact lookup,
bounded traversal, and impact queries; run-scoped staging with atomic
publication and durable restart semantics; publication receipts, freshness
checks, affected-test queries, immutable publication snapshots with
integrity hashes, and optimistic concurrent-publication ordering.
