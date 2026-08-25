# Penghou.Hetu Benchmarks

The reproducible BenchmarkDotNet harness lives in
`benchmarks/Penghou.Hetu.Benchmarks`. It exercises native LadybugDB unit
replacement, exact qualified-name lookup, bounded traversal, deletion plus
reinsertion, and database reopen.

Run the complete matrix in Release mode:

```powershell
dotnet run -c Release --project benchmarks/Penghou.Hetu.Benchmarks -- --job short
```

## Schema 3 baseline

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
Git. Re-run the matrix when changing the Ladybug schema, serialization, native
package, batching strategy, or query shape.
