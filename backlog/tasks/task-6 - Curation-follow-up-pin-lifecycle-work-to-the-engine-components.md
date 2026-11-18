---
id: TASK-6
title: 'Curation follow-up: pin lifecycle work to the engine components'
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-10
dependencies:
  - TASK-1
priority: low
ordinal: 6000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Keep the Groma map current with the code: after the native engine-lifecycle work landed, the map must show the provider and lifecycle components and how they relate to `EngineRuntime`, and any component a scan introduces gets its meaning written through the CLI rather than left as an empty element.

The container-modelling question this row used to be blocked on is closed: the shared class library now maps as one declared container (decision-8), and its components sit under it rather than under an unidentified one. What is left is curation, not modelling: re-scan, write the missing descriptions and overviews for the lifecycle components, and add the relationships the scan cannot see (who asks the provider to switch, and what the scheduler reads). The map is a reviewed artifact: it moves only through the `groma` CLI, and the seat that runs the CLI commits the result.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each lifecycle component in the map names the source files it owns: the provider seam, the coordinator, the delegated-exec provider and the Docker provider
- [ ] #2 The relationships from those components to EngineRuntime are on the map, so a reader can follow a switch from the runtime to the provider that performs it
- [ ] #3 Any re-parenting that changes the map model is recorded as a decision record, so the reasoning is reversible without being re-derived
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Curated through the CLI in this push: the `CapabilityScan` element (the admission refusal's one-way rule) had been left as an empty scanned file and now carries a description and overview.

Measured 08:16Z: an untracked Groma component note for a new shared-library type sits in the working tree and is tracked nowhere — git ls-files groma lists 34 files and names none for it. Architecture changes go through the Groma CLI alone: rerun the scan or add route, or delete the file. Keeping it out of the code commit was right; leaving it untracked forever is not.
<!-- SECTION:NOTES:END -->
