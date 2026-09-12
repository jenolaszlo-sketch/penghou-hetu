# Native graph mirror (experimental preview)

`LatticeCodeGraphStore` can serve `TraverseAsync` without evidence filters
from real LatticeDB graph structure instead of the materialized projection.
Enable it with `LatticeDbStoreOptions.UseNativeTraversal`. Everything else,
including provenance-bound queries, always uses the projection.

## Design

The mirror is derived state, never the source of truth:

- The command log stays authoritative for staging, publication, recovery,
  and ordering. Staged facts are never mirrored; only complete publications
  are.
- Each published `CodeGraphNode` becomes one `HetuGraphNode` native node
  carrying its full JSON; each `CodeGraphEdge` becomes one native edge typed
  by its kind and carrying its JSON. Native ids are engine-assigned, so a
  `HetuMirror` row maps Hetu identities to native identities per repository.
- The mirror is rebuilt from published units on every publication and on
  reopen when its row is missing. A corrupt or stale mirror is recoverable
  by rebuilding; it can never poison the log.
- Mirror writes join the same native transaction as the log writes, so a
  failed completion rolls both back together.
- Evidence-filtered traversals stay on the projection: evidence lives in
  edge facts, and traversal results do not carry edge properties.

## Measured reality

The first mirror is ~300x slower than the projection on small bounded
traversals (2.1 ms vs 6 us) while scaling equally flat. Cost breakdown:

1. One native round trip per BFS hop instead of in-memory adjacency.
2. One JSON property read plus `System.Text.Json` deserialization per
   returned fact.
3. A read transaction plus write-gate acquisition per query.

None of this is fundamental to native querying. The ordered improvement
path is: push BFS into a single native/Cypher traversal, bulk-read fact
payloads, then replace JSON text with a binary fact encoding. Until then
the flag stays preview and default-off, and the projection remains the
serving path.

## What would promote it

- Server-side bounded traversal with parity-proven bounds and ordering.
- Native fact storage as the source of truth (removing JSON replay from
  reopen), migrated through the existing snapshot export/import path.
- Provenance served natively once contributor evidence is queryable.
