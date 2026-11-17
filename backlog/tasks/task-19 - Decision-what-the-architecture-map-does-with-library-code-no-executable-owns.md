---
id: TASK-19
title: 'Decision: what the architecture map does with library code no executable owns'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
labels:
  - architecture
dependencies: []
priority: high
ordinal: 19000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The map groups the library behind an entry labelled "Unidentified container", which is what the scanner produces when nothing in the repo names a container. The library is not a container: it is the product. Three ways out, pick one: (A) accept the label as a zone and change nothing; (B) teach the scanner that the library is a component the executables depend on, so it maps as itself; (C) split the map per executable, with the library as a shared component under each. It is your call because the map files are tool-owned and edits need your approval. Unblocks TASK-6.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One of A, B or C is chosen, in one line
<!-- AC:END -->

## Do this (about 5 minutes)

1. Run `groma view` in the repo (or `groma web` for the browser map) and find how the library's own
   components hang off a container the map labels as unidentified.
2. The map lives under `groma/systems/inference-router/` — `system.md`, `containers/`, `components/`.
   While you are there: `containers/` holds near-duplicates (`air-cli` and `…-cli`, `air-server` and
   `…-server`), which is the same mess pointing the other way.
3. Reply with one letter: **A** accept the label and change nothing, **B** declare a container for the
   library so it maps as itself, **C** split the map per executable with the library shared under each.

Answer format: `TASK-19: B`. All map edits go through the `groma` CLI and are mine to do.
