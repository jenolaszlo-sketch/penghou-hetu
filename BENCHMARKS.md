# Penghou.Hetu Benchmarks

The reproducible BenchmarkDotNet harness lives in
`benchmarks/Penghou.Hetu.Benchmarks`. It exercises durable LatticeDB unit
replacement, exact qualified-name lookup, bounded traversal, deletion plus
reinsertion, and database reopen.

Run the complete matrix in Release mode:

```powershell
dotnet run -c Release --project benchmarks/Penghou.Hetu.Benchmarks -- --job short
```

## LatticeDB schema 1 baseline (current)

Measured on 2026-09-13 using LatticeDbSharp 0.1.1, .NET 10.0.11,
Windows 11, and an Intel Core Ultra 5 125H, with a short smoke job
(`--job short`; write operations show high variance across the 3 iterations).
Results are local engineering baselines, not portable performance guarantees.

| Operation | 100 nodes | 1,000 nodes |
|---|---:|---:|
| Replace index unit | 7.66 ms | 52.5 ms |
| Exact qualified-name lookup | 0.0006 ms | 0.004 ms |
| Bounded traversal | 0.006 ms | 0.006 ms |
| Native bounded traversal (preview mirror) | 2.11 ms | 2.05 ms |
| Delete and reinsert unit | 9.43 ms | 55.2 ms |
| Reopen and health check | 38.7 ms | 89.9 ms |

The preview native mirror is two orders of magnitude slower than the
projection on small bounded traversals: every hop is a native round trip and
every returned fact pays a JSON property read plus `System.Text.Json`
deserialization. It scales flat like the projection (2.11 ms at 100 nodes,
2.05 ms at 1,000), so the design is sound but per-fact overhead dominates.
Closing the gap needs server-side BFS in one query instead of per-hop calls,
bulk fact reads instead of one property read per fact, or a binary fact
encoding instead of JSON text. See `docs/native-graph-mirror.md`.

The fixture uses a chain graph with one fewer edge than nodes. Traversal starts
at the middle node and is fixed at depth 4, 25 nodes, and 50 edges. Lookups and
bounded traversal are served from the materialized in-memory projection, so
they stay flat as the stored unit grows. Writes remain approximately linear
because unit replacement transactionally persists owned command-log records.
Reopen cost is dominated by full log replay and JSON deserialization; promoting
facts to native graph storage would remove that ceiling.

## LatticeDB schema 1 baseline (2026-09-10, LatticeDbSharp 0.1.0-preview.1)

| Operation | 100 nodes | 1,000 nodes |
|---|---:|---:|
| Replace index unit | 6.70 ms | 52.5 ms |
| Exact qualified-name lookup | 0.0006 ms | 0.004 ms |
| Bounded traversal | 0.006 ms | 0.007 ms |
| Delete and reinsert unit | 8.44 ms | 58.0 ms |
| Reopen and health check | 28.3 ms | 85.8 ms |

## LadybugDB schema 3 baseline (historical, pre-port)

Measured on 2026-08-25 using LadybugDB 0.19.1, .NET 10.0.11, Windows 11,
and an Intel Core Ultra 5 125H. Results are local engineering baselines, not
portable performance guarantees.

| Operation | 100 nodes | 1,000 nodes |
|---|---:|---:|
| Replace index unit | 118 ms | 1.19 s |
| Exact qualified-name lookup | 0.999 ms | 0.987 ms |
| Bounded traversal | 4.35 ms | 5.93 ms |
| Delete and reinsert unit | 137 ms | 1.39 s |
| Reopen and health check | 42.6 ms | 55.0 ms |

The fixture uses a chain graph with one fewer edge than nodes. Traversal starts
at the middle node and is fixed at depth 4, 25 nodes, and 50 edges. Primary-keyed
incoming/outgoing adjacency projections keep lookup and bounded traversal nearly
flat as the stored unit grows. Writes remain approximately linear because unit
replacement transactionally refreshes owned facts and affected adjacency rows.

Benchmark output belongs under `BenchmarkDotNet.Artifacts`, which is ignored by
Git. Re-run the matrix when changing the LatticeDb schema, serialization, native
package, batching strategy, or query shape.
