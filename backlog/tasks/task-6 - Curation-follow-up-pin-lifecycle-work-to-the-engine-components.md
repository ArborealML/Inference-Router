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
