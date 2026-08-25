# Workspace experiment - design notes

Companion to the workspace-experiment section in ROADMAP.md. This file holds
the detailed semantics the roadmap intentionally keeps at decision altitude.
Nothing here is a product contract until the first vertical slice proves the
experiment useful.

## Extension order after the first slice

1. Add and delete sources, with base hashes and explicit conflict detection.
2. Immutable, append-only workspace revisions and a movable logical head.
3. Named checkpoints and rollback by moving the head, never by reversing graph
   mutations.
4. Branches from one base revision so Solo can compare candidate edits.
5. Persist and recover workspace source changes through a dedicated bounded
   workspace/blob store.
6. First-class semantic graph diffs between publications or workspace
   revisions.
7. Optional incremental plugin sessions only if profiling shows rebuilding
   affected project compilations is a material bottleneck.

## Revision record

A workspace revision retains at least:

- WorkspaceId
- BasePublicationId
- ParentRevisionId
- RevisionId
- SourceChanges with base/content hashes
- WorkspaceGraphPublicationId
- Diagnostics and relationship-coverage metadata

## Graph diffs

CodeGraphDiff is a first-class derived result. It describes added and removed
facts, changed relationships, new dependencies, affected callers and tests,
diagnostic changes, and impact-radius changes while retaining both
publication/revision identities and contributor provenance.

## Source persistence separation

Workspace source persistence is a separate responsibility from graph
persistence. Ladybug does not store source blobs and ICodeGraphStore gains no
blob methods; introduce ICodeWorkspaceStore only after revision, retention,
privacy, encryption, size-budget, and cleanup semantics are designed. A
recovered workspace must validate its pinned base publication and source
hashes before refresh.