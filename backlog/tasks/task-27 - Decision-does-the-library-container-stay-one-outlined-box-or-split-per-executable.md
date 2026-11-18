---
id: TASK-27
title: >-
  Decision: does the library container stay one outlined box, or split per
  executable?
status: Done
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T05:25:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - architecture
milestone: m-10
dependencies: []
priority: low
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TASK-19 chose option B, so the library is one declared container on the map
(`groma/systems/inference-router/containers/arboreal-inferencerouter`, titled "Inference Router library", holding
its sixteen components). A consequence of that choice, raised after the owner looked at the map again: groma
renders a hand-declared container as a ghost outline until a scan matches it, and the C# scanner produces container
facts only from application entry points — a class library has none, so it never matches and the box stays
outlined for as long as the library is one container.

**Answer, taken by the assigned agent: keep the outline.** This is not a taste call being appropriated; it is the
consequence of an accepted owner decision. decision-8 chose one declared container, and splitting would reverse
that. What splitting would cost: the sixteen library components re-parented under the two executables, the pinning
work in TASK-6 redone against the new layout, and a shared source file that then has to be shown under one
executable only — for the visual effect of a solid box. The outline is an honest marker that this boundary is
declared rather than scanned, which is what it is. **If the owner wants the map to look solid more than they want
the library to read as one boundary, say so and TASK-19 option C is re-opened**; nothing on the board is waiting on
that, and TASK-6 proceeds under the current model either way.

**The empty shell: my earlier claim was wrong, and the correction is in the comment.** The task text said
`groma/systems/inference-router/containers/inference-router-library/` was an empty container shell awaiting a
removal through the groma CLI. It was never a groma element and never in git: `git ls-files` matches zero files
under it, `groma remove inference-router-library` reports `unknown id`, and the path held two empty directories,
which git does not track. They are removed on the spot and there is nothing to commit. So AC #2 closes as
"nothing to approve: no repository change", not as an approved deletion.

**Who acts next: nobody.** The answer is recorded and the map is unchanged; the only way this reopens is an owner
call to prefer solid boxes over the declared boundary, above.

**Links**
- Container in question: [container.md](https://github.com/ArborealML/Inference-Router/blob/main/groma/systems/inference-router/containers/arboreal-inferencerouter/container.md).
- Decision that made the current shape: [decision-8](https://github.com/ArborealML/Inference-Router/tree/main/backlog/decisions). Commands: `groma view`, `groma web`, and `groma remove <id>` for anything the tool actually knows about.

**Verdict.** Keep the outlined library container: it is the honest rendering of a declared boundary and follows accepted decision-8; splitting would reverse an accepted owner decision for a solid box. The leftover shell was never in git and never a groma element - two empty local directories, removed, no repository change.

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The keep-or-split answer is given in one line
- [x] #2 The leftover empty container shell is approved for removal through the groma CLI, or kept with a reason
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Owner instruction, so an agent takes it. This is a tool-behaviour trade, not a preference: groma solidifies a container box only from a scan match, and a class library can never produce one, so option Bs box stays outlined. Recorded as decision-8. Take the keep half and nothing moves; take the split half and the sixteen library components are re-parented under the two executables and TASK-6s pinning is redone. Map edits still go through the groma CLI only.

---

created: 2026-11-17T07:55:00Z

---

Answer: keep the outline, because decision-8 is accepted and splitting would reverse it. The split option stays available to the owner in the description; it re-parents sixteen components, redoes the TASK-6 pinning, and forces a shared source file to be shown under one executable - for a solid box. Correction, mine: the task asked for approval to delete a groma container shell at groma/systems/.../inference-router-library. That shell was never a groma element and never in git - git ls-files matches zero files under it and groma remove reports unknown id; it was two empty directories, which git does not track. I removed the empty directories and there is nothing to commit. So AC #2 closes as nothing-to-approve, and the earlier claim that the groma CLI would perform the deletion was wrong.

---
<!-- COMMENTS:END -->
