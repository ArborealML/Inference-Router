---
id: TASK-6
title: 'Curation follow-up: pin lifecycle work to the engine components'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T00:35:00Z'
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
"Unidentified container" — the scan gives the two executables a container each and never sees one for
the class library, because a library that is compiled into both executables has no runtime boundary of
its own. Probed at 03736f2 in a throw-away worktree: a hand-drafted `router-library` container is never
matched by a later scan (`groma accept router-library` → "no scan match"), and relocating a curated
component is refused (`groma edit routercore --parent router-library` → "cannot move 'routercore'
because it has authored meaning"). So clearing the group means either a scanner/groma option that
declares a container for a class-library project (we do not have one), or a re-authoring pass: strip
each component's overview, move it into a container, then rewrite its overview, relationships and the
flow links that point at the old paths. Decision needed: is the .NET library a container in its own
right, or do its components belong to the container that actually hosts them (`air-server`, with the
CLI reaching them by relationship)?
