# Penghou.Hetu

Penghou.Hetu is an embedded, language-neutral code knowledge graph for .NET.
It turns source repositories into a normalized, queryable graph for repository
understanding, dependency discovery, impact analysis, and context selection.

Hetu defines what code means in the graph. Language plugins define how those
facts are discovered.

```text
Source repository
       │
       ▼
Language extraction session
       │
       ▼
Owned graph-fact batches
       │
       ▼
Hetu validation and persistence
       │
       ▼
Bounded deterministic queries
       │
       ▼
Solo and other consumers
```

Hetu does not define a universal AST and does not expose parser-specific types.
A C# plugin may use Roslyn while future plugins may use compiler APIs, generated
ANTLR parsers, hand-written parsers, or other deterministic extraction tools.

## Status

Hetu is at the initial architecture and scaffolding stage. The public contracts
are not stable and no package has been published yet. See [ROADMAP.md](ROADMAP.md)
for the implementation milestones and semantic invariants.

## Planned packages

| Package | Purpose |
| --- | --- |
| `Penghou.Hetu.Abstractions` | Parser-independent graph vocabulary, evidence, extraction sessions, and sinks |
| `Penghou.Hetu` | Indexing orchestration, validation, in-memory storage, and query services |
| `Penghou.Hetu.CSharp` | Roslyn-based C# extraction plugin |
| `Penghou.Hetu.Ladybug` | Embedded LadybugDB graph-store provider |
| `Penghou.Hetu.Testing` | Reusable graph-store and plugin contract tests |

The future `Penghou.Hetu.Generator` project is reserved for deterministic
generation of language plugins from grammars and declarative graph mappings.
It is intentionally outside the first milestone.

## Architectural boundaries

- Core abstractions have no Roslyn, ANTLR, LadybugDB, LSP, SCIP, or AI dependency.
- Plugins receive repository-aware extraction sessions so semantic analyzers can
  resolve project-wide and cross-file relationships.
- Plugins emit normalized facts; they never write directly to a graph database.
- Hetu owns graph vocabulary, validation, physical identity, transactions,
  incremental replacement, and query semantics.
- Persistence providers implement Hetu's store contract without leaking their
  query language through ordinary consumer APIs.
- All traversals are bounded and deterministically ordered.
- Evidence records how each relationship was discovered without overstating
  syntax-only or heuristic information as compiler-resolved truth.

## Intended use

Hetu will help answer questions such as:

- What does this type contain?
- What calls this method, and what does it call?
- Which types implement this interface?
- What references or depends on this symbol?
- What bounded neighborhood is likely affected by a change?
- Which source declarations should Solo include in model context?

## Relationship to the Penghou stack

```text
Baize   provider and model communication
Nuwa    deterministic structured-output repair
Zhinu   durable workflow execution
Cangjie durable contextual knowledge and provenance
Hetu    source-code structure, relationships, and impact
Solo    software-engineering orchestration and interpretation
```

Hetu may supply graph-derived observations to Cangjie or Solo, but it does not
become a general memory store or workflow engine.

## Initial non-goals

- universal compilation or exhaustive data-flow analysis;
- runtime AI parsing or plugin generation;
- embeddings or vector search;
- automatic plugin or NuGet downloading;
- LSP, SCIP, or Tree-sitter integration;
- AI-generated architectural labels or component clustering;
- generated non-C# language packages.

The first useful release will index real C# repositories, persist a stable
graph, survive repeat and incremental indexing, and answer bounded structural
and dependency queries.
