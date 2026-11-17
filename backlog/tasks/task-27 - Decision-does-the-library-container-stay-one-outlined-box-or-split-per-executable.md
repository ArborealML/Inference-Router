---
id: TASK-27
title: >-
  Decision: does the library container stay one outlined box, or split per
  executable?
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:25:00Z'
updated_date: '2026-11-17T05:55:00Z'
labels:
  - architecture
dependencies: []
priority: low
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TASK-19 chose option B, so the library is now one declared container on the map (groma/systems/inference-router/containers/arboreal-inferencerouter, titled "Inference Router library", holding its sixteen components). One consequence of that choice is permanent and was raised after the owner looked at the map again: groma renders a hand-declared container as a ghost outline until a scan matches it, and the C# scanner produces container facts only from application entry points — a class library has none, so accepting it prints "no scan match". The box stays outlined for as long as the library is one container. Decide whether that is acceptable. **Keep** changes nothing: the library reads as one outlined box, and the outline is an honest marker that the boundary is declared rather than scanned. **Split** (TASK-19 option C) moves the library components under each executable container so every box is solid — at the cost of a shared source file appearing under one executable only, and the pinning work in TASK-6 being redone against the new layout. Nothing on the board waits on this: TASK-6 proceeds under the current model and would be reworked only if you choose to split.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The keep-or-split answer is given in one line
- [ ] #2 The leftover empty container shell is approved for removal through the groma CLI, or kept with a reason
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Owner instruction, so an agent takes it. This is a tool-behaviour trade, not a preference: groma solidifies a container box only from a scan match, and a class library can never produce one, so option Bs box stays outlined. Recorded as decision-8. Take the keep half and nothing moves; take the split half and the sixteen library components are re-parented under the two executables and TASK-6s pinning is redone. Map edits still go through the groma CLI only.

---
<!-- COMMENTS:END -->

## Links

- Container in question: [container.md](https://github.com/ArborealML/Inference-Router/blob/main/groma/systems/inference-router/containers/arboreal-inferencerouter/container.md).
- Empty shell awaiting an approve-or-leave call: [containers/inference-router-library/](https://github.com/ArborealML/Inference-Router/tree/main/groma/systems/inference-router/containers/inference-router-library/).
- Decision that made the current shape: [decision-8](https://github.com/ArborealML/Inference-Router/tree/main/backlog/decisions). Commands: `groma view`, `groma web`.

## Do this (about 4 minutes)

1. Run `groma view` in the repo (or `groma web` for the browser map) and look at the library box: it renders as a ghost outline, not a solid container.
2. Answer one line: `TASK-27: keep the outline` or `TASK-27: split per executable`.
3. One approval while you are in there: `groma/systems/inference-router/containers/inference-router-library/` is an empty shell left by the earlier experiment — no container file, no components, and the real library container is the one titled "Inference Router library". Say `clean up the shell` or `leave it` and I run the deletion through the `groma` CLI; map files are tool-owned, so the edit is mine and the approval is yours.

What each answer changes: **keep** leaves the map as it is; **split** re-parents the sixteen library components under the two executables and the TASK-6 pinning is redone; **clean up** removes a directory that renders as nothing.
