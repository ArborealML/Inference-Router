---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-18T21:09:00Z'
labels:
  - concurrency
  - testing
milestone: m-2
dependencies:
  - TASK-67
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
- [ ] #5 A throwing pre-install marker hook propagates its exception, clears the marker outside the transition gate, makes zero provider launches, and permits the next claim to succeed.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**The seam is owned by TASK-67, so nobody builds it twice.** TASK-67 adds the deterministic hooks — `AfterEndCommit` past the end commit and before the readiness settle, a pre-wait hook between the fast-path miss and the lock, and an injected launcher at the scheduling boundary — and makes the claim decision and the marker come from one snapshot. #1 and #2 close on that commit with its mutation evidence; this row keeps #3 (the withdrawn test) and #4 (the marker clause at its own seam).

Review of [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2) verified rollback installs the candidate when the pre-install hook throws. The existing throwing-hook test injects after private state installation. Add the earlier-hook variant after TASK-87 measurements.

Review of integrated lifecycle authority found that a hook or scheduling failure before any provider mutation can clear incumbent proof. Exercise an already-proven incumbent with observed windows and ensure a provably unstarted switch preserves it unless newer evidence contradicts it.
<!-- SECTION:NOTES:END -->
