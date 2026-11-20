---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: Preparing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-20T21:19:00Z'
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
Shutdown is auditing existing switch-claim and readiness evidence on 6588da3, which contains the TASK-67 prerequisites. Identify what each test observes before, during and after the transition gate. Distinguish a waiter rereading state after commit from exclusion during commit, and a throwing post-install hook from failure before installation. Check the readiness marker against the healthy snapshot available after switch proof. Trace the dropped scheduling-dependent test separately from the forced compare-and-swap loser gap in SPEC §15.
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
