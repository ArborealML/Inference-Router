---
id: TASK-6
title: 'Curation follow-up: pin lifecycle work to the engine components'
status: Blocked
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T02:30:00Z'
labels: []
dependencies:
  - TASK-1
ordinal: 6000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After the native engine-lifecycle work lands, re-scan and make sure the map shows the new provider/registry components and their relationships to EngineRuntime.
<!-- SECTION:DESCRIPTION:END -->

## Status notes

Blocked in practice on TASK-1 (the lifecycle provider): until the provider/registry components exist
there is nothing to pin. When it does run, the map edits go to @claire for approval before commit —
the map is a reviewed artifact, not agent scratch.

Open finding, needs @claire's call before any map edit: the library's eleven components sit directly
under the system (their metadata says `parent: inference-router`), so the rendered map files them under
"Unidentified container" — the scan gives each executable an entry point and core places those in a
container, but the class library has no executable entry of its own, being compiled into both
executables. Probed at 03736f2 in a throw-away worktree: a hand-drafted `router-library` container did
not match any scan finding in that probe (`groma accept router-library` → "no scan match"), and
relocating a curated component is refused (`groma edit routercore --parent router-library` → "cannot
move 'routercore' because it has authored meaning"). No supported in-place migration for curated
components was found in the current tooling, and stripping each component's description to force the
move is not a supported or lossless sequence, so this is a modelling decision first and a tooling
question second.

Decision needed: what is a container here. The C4 guidance treats a shared assembly as *not* a container,
which argues for keeping the shared components unfiled until we choose a simplification, or for filing
them under the process that actually hosts them — noting that `air serve` runs the HTTP host inside the
CLI process and `air config` loads configuration locally, so the CLI is not merely a remote client of the
library and the map cannot show one source file owned by both executables.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on TASK-19 — the next move is the map-model decision, which belongs to a human. Work here resumes when that task is closed.

Two things gate this: the map-model decision, and the lifecycle work it is meant to pin (TASK-1), which is now unblocked and mine to start.
<!-- SECTION:NOTES:END -->
