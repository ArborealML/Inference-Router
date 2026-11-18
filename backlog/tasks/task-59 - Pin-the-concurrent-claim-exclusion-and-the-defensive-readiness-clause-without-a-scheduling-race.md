---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-18T00:43:00Z'
labels:
  - concurrency
  - testing
dependencies:
  - TASK-61
priority: medium
ordinal: 59000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
fd5e0cd made the claim/coalesce decision read switch state inside the transition gate, so a second waiter reaching that decision while a switch commits its end is excluded by construction. The construction is not pinned. A test that started a second waiter inside the window reproduced the hazard, but its outcome was decided by the thread pool's own scheduling — two blocked pool threads starved its continuation by seconds — so it was removed rather than shipped as a flake, and SPEC 15 records the drop. Today the ordering rests on a code comment plus a moved read. What is wanted is a seam that makes the interleaving explicit and deterministic — an internal hook like the one the marker-before-claim barrier already uses, or a single-threaded dispatcher clock — not a sleep. Same treatment for the marker half of IsReadyFor, which no public path reaches now that every marker state carries Healthy=false.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a deterministic test shows a second waiter cannot take the claim-or-coalesce decision while a switch commits its end, with no dependence on thread scheduling
- [ ] #2 the test fails when the state read is moved back outside the gate, shown as mutation evidence
- [ ] #3 the dropped test is either restored or withdrawn out loud in SPEC 15 with this task cited
- [ ] #4 the marker clause of the readiness rule is pinned at its own seam, or recorded as unreachable with the reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**The seam is owned by TASK-61, so nobody builds it twice.** TASK-61's commit adds the deterministic claim/end hooks (`AfterEndCommit` after the end commit and before the readiness settle, `BeforeLaunch` before the launch is scheduled) and pins the claim-or-coalesce decision inside the transition gate. #1 and #2 close on that commit, with its mutation evidence cited here; this row then keeps only #3 (the withdrawn test) and #4 (the marker clause at its own seam). It depends on TASK-61 for exactly that reason, not for the readiness rule itself.
<!-- SECTION:NOTES:END -->
