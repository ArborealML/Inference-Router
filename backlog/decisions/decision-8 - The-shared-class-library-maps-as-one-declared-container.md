---
id: decision-8
title: 'The shared class library maps as one declared container'
date: '2026-11-17T05:48:00Z'
status: accepted
---
## Context

The architecture scanner produces container facts only from application entry points. A class library has
no entry point of its own — it is compiled into both executables — so its routing core, queues,
scheduler, config, events and lifecycle providers were all filed under an "Unidentified container" in the
rendered map. A hand-drafted container for the library could not be accepted (`no scan match`), and
curated components refuse to move while they carry authored meaning. Decided as TASK-19 option B; map
commits `69a1cba`, board record `5eb188d`.

## Decision

Options considered:

1. **Accept the label** — leave the library's components under the unidentified group.
2. **Declare a container for the library** and move its components into it through the tool, so the
   library maps as one thing the executables depend on.
3. **Split the map per executable** with the library's components under each — every box is solid, but a
   shared source file then appears under one executable only.

**Chosen: 2.** The library's sixteen components now sit in one declared container, and nothing in the
library hangs off an unidentified one.

## Consequences

- The declared container renders as a **ghost outline**, permanently: only a scan match solidifies a box,
  and a library can never produce one. The outline is an honest marker that the boundary is declared
  rather than scanned — that trade is the decision, not an artifact defect.
- Moving curated components required clearing and restoring their authored text because the tool only
  moves components without meaning; descriptions were re-authored from pre-change copies.
- Follow-up is open as TASK-27: keep the outlined box, or take option C and re-parent the components under
  each executable, which would redo the pinning work in TASK-6.
- Moving components to a new parent needs @claire's approval, since the map files are tool-owned and the
  map is a reviewed artifact rather than agent scratch.
