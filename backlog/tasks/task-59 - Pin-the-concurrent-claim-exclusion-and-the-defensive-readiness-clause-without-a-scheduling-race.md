---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: To Do
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-20T23:03:00Z'
labels:
  - concurrency
  - testing
milestone: m-2
dependencies:
  - TASK-67
priority: high
ordinal: 59000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The [SPEC citation for the withdrawn probe](https://github.com/ArborealML/Inference-Router/pull/65) landed at e8a1fb8, and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44833139603) passed. The [readiness fixture](https://github.com/ArborealML/Inference-Router/pull/62) landed at 4cd1e85 with its original marker-only reversal and restored pass. Both focused cases passed after the fixture repair and all three [main checks](https://github.com/ArborealML/Inference-Router/actions/runs/44829934258) passed. The remaining work concerns concurrent claim exclusion, moving the state read outside the gate, and rollback before claim installation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a deterministic test shows a second waiter cannot take the claim-or-coalesce decision while a switch commits its end, with no dependence on thread scheduling
- [ ] #2 the test fails when the state read is moved back outside the gate, shown as mutation evidence
- [x] #3 the dropped test is either restored or withdrawn out loud in SPEC 15 with this task cited
- [x] #4 the marker clause of the readiness rule is pinned at its own seam, or recorded as unreachable with the reason
- [ ] #5 A throwing pre-install marker hook propagates its exception, clears the marker outside the transition gate, makes zero provider launches, and permits the next claim to succeed.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Preserve the held-switch public readiness test. In the opt-in fake, signal registration only after enqueueing the exact launch gate and guarantee cleanup on every exit. In the controlled comparison, acknowledge that the old ReleaseOne ran while the queue was empty. On the repaired path require the registered signal to release the exact queued gate before cleanup can run. Qualify TheMarkerGoesUpBeforeTheEngineIsClaimed and FailedPollDuringASwitchCannotWithdrawTheSwitchsPublishedWindows.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**The seam is owned by TASK-67, so nobody builds it twice.** TASK-67 adds the deterministic hooks — `AfterEndCommit` past the end commit and before the readiness settle, a pre-wait hook between the fast-path miss and the lock, and an injected launcher at the scheduling boundary — and makes the claim decision and the marker come from one snapshot. #1 and #2 close on that commit with its mutation evidence; this row keeps #3 (the withdrawn test) and #4 (the marker clause at its own seam).

Review of [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2) verified rollback installs the candidate when the pre-install hook throws. The existing throwing-hook test injects after private state installation. Add the earlier-hook variant after TASK-87 measurements.

Review of integrated lifecycle authority found that a hook or scheduling failure before any provider mutation can clear incumbent proof. Exercise an already-proven incumbent with observed windows and ensure a provably unstarted switch preserves it unless newer evidence contradicts it.
<!-- SECTION:NOTES:END -->
