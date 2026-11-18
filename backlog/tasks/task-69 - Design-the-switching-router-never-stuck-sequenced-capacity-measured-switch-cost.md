---
id: TASK-69
title: >-
  Design the switching router: never stuck, sequenced capacity, measured switch
  cost
status: Done
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-117
priority: high
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The design review and owner ruling are complete. Decision-62 records the failure contract. Implementation and runtime qualification belong to the linked tasks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the plan sits in this row before any code, and the design review at the required tier has returned and been dispositioned
- [x] #2 the plan names what a failed swap does to in-flight and to waiting requests, in wire and feed terms
- [x] #3 the plan honours four lanes for the whole box and one loaded model at a time, and says how capacity is sequenced
- [x] #4 the posture rows this goal un-parks are answered or explicitly kept open by the plan, not worked around
- [x] #5 Automatic switching is a core routing capability. After a failed swap, pending work for other demonstrably available models is served, with an observable outcome for the failed target.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Retain a single resident model and four host lanes. TASK-25/64 fence new admission and drain existing inference before a stop; lifecycle work holds no inference lane. Preserve a still-proven incumbent on preflight failure. If A fails after stop, terminate each affected A ticket once with a matching feed outcome and quarantine that exact target. TASK-26 implements the latch and target-scoped reset. TASK-110/114 prove B under a separate fenced transaction. TASK-51 bounds queued waits; TASK-71 measures phase cost and TASK-72 combines it with age and batching. For eight B waiters and one A, serve two four-lane B waves then the eligible aged A. Time limits and stream preemption remain separate decisions. TASK-68 covers coherent capability reads, TASK-77 authority and TASK-78 fleet placement.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[Accepted failure policy](https://github.com/ArborealML/Inference-Router/blob/main/backlog/decisions/decision-62%20-%20Quarantine-a-failed-switch-target-and-recover-other-models-under-fresh-proof.md) The reviewed design answers AC #5; TASK-26 and TASK-114 carry the implementation and negative controls. The review ruled out a cooled same-target retry. A committed stream uses its in-band error contract and an earlier ticket terminal stands. A deadline bounds the wait, while an unbounded stream prevents promising a fixed service time. TASK-112 gates the real pi-clm swap and TASK-113 follows it.
<!-- SECTION:NOTES:END -->
