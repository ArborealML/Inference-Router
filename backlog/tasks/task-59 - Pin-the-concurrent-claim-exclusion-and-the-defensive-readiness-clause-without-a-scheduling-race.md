---
id: TASK-59
title: >-
  Pin the concurrent-claim exclusion and the defensive readiness clause without
  a scheduling race
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T22:49:00Z'
updated_date: '2026-11-20T23:49:00Z'
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
Wake on implementation release after primary-host enrollment work. Independent review passed the plan for concurrent claim exclusion and the protected state read. Both criteria remain open. The [withdrawn-probe citation](https://github.com/ArborealML/Inference-Router/pull/65) and [marker readiness fixture](https://github.com/ArborealML/Inference-Router/pull/62) already landed. Rollback before claim installation is separate.
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
Hold caller B at the semaphore hook while A commits a failed launch. Observe the end commit independently. With the protected read, require B to reacquire, launch a second attempt and become ready. Move only the state read before the hook with the fixture unchanged. Require B to report A’s exact launch_failed with one acquisition, one launch and one terminal record. Restore the exact source and pass the same focused case. Bound release and join in finally; use explicit ordering rather than elapsed time.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reuse the existing claim and commit hooks. Review of [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2) distinguished rollback before private state installation from the existing post-install throwing-hook case. The remaining rollback criterion requires separate evidence, including preservation of a proven incumbent after a provably unstarted switch unless newer evidence contradicts it.
<!-- SECTION:NOTES:END -->
