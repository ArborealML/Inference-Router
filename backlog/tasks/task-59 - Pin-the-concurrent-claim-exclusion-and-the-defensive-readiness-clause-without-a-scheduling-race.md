---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-20T22:06:00Z'
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
Wake on independent review of the fixture repair plan. Shutdown verified the clean 124d10f checkout and preserved the original readiness assertion and its marker-only reversal. The [failed Release job](https://github.com/ArborealML/Inference-Router/actions/runs/44824380288) had one failure and 1,809 passes. TheMarkerGoesUpBeforeTheEngineIsClaimed saw readiness false after 30 seconds. The fake increments LaunchCount before queueing its gate, so ReleaseOne can miss. The CI log does not establish the interleaving. After plan approval, qualify the failing case and the new readiness case, demonstrate the difference under controlled gate registration, then require correction review and green hosted CI on the corrected [draft](https://github.com/ArborealML/Inference-Router/pull/62).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a deterministic test shows a second waiter cannot take the claim-or-coalesce decision while a switch commits its end, with no dependence on thread scheduling
- [ ] #2 the test fails when the state read is moved back outside the gate, shown as mutation evidence
- [ ] #3 the dropped test is either restored or withdrawn out loud in SPEC 15 with this task cited
- [ ] #4 the marker clause of the readiness rule is pinned at its own seam, or recorded as unreachable with the reason
- [ ] #5 A throwing pre-install marker hook propagates its exception, clears the marker outside the transition gate, makes zero provider launches, and permits the next claim to succeed.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend FailedPollDuringASwitchCannotWithdrawTheSwitchsPublishedWindows. Check the matching model roster and fitting window, then call public IsReadyFor with the same model and profile while the marker is held and after settlement. Expect false then true. Remove only the marker check in ReadyIn, verify the held assertion fails, and restore the exact original source for a final pass.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**The seam is owned by TASK-67, so nobody builds it twice.** TASK-67 adds the deterministic hooks — `AfterEndCommit` past the end commit and before the readiness settle, a pre-wait hook between the fast-path miss and the lock, and an injected launcher at the scheduling boundary — and makes the claim decision and the marker come from one snapshot. #1 and #2 close on that commit with its mutation evidence; this row keeps #3 (the withdrawn test) and #4 (the marker clause at its own seam).

Review of [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2) verified rollback installs the candidate when the pre-install hook throws. The existing throwing-hook test injects after private state installation. Add the earlier-hook variant after TASK-87 measurements.

Review of integrated lifecycle authority found that a hook or scheduling failure before any provider mutation can clear incumbent proof. Exercise an already-proven incumbent with observed windows and ensure a provably unstarted switch preserves it unless newer evidence contradicts it.
<!-- SECTION:NOTES:END -->
