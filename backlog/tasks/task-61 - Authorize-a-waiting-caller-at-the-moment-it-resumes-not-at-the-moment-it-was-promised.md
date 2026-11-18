---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T05:55:00Z'
labels:
  - concurrency
  - scheduling
dependencies:
  - TASK-25
  - TASK-72
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner decided the posture this row waited on: a switch the engine cannot confirm is **refused** (decision-27), so the capability and the answer to the caller can no longer disagree. The invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** The first attempt landed at 042e212 and ping-ponged (decision-36); this row is the narrowed half plus the gap it leaves.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The cannot-confirm refusal landed first in its own commit; this row changes only how a resuming waiter is classified
- [x] #2 A switch whose own attempt ended false ends Failed once, so a caller with no ceiling cannot relaunch forever
- [ ] #3 A waiter whose own attempt succeeded takes its own turn rather than a refusal when something other than a later claim contradicts the target, pinned where a second claim wins the engine
- [ ] #4 The resume check is pinned by a gate between the readiness await and the snapshot read, so the pin cannot pass on thread scheduling
- [ ] #5 A switch marker raised by a LATER claim never withdraws the proof a waiter's own switch published: a caller that holds its own proof is not sent behind another caller's transaction
- [ ] #6 Two callers wanting different models on a one-model engine produce no alternating launch sequence — pinned as a launch sequence, not as the absence of a timeout
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Fix-forward inside this row, two commits — 042e212 is not narrowed into the refusal work (that is TASK-62, shipped and reviewed at 3dce284). **Commit 1, green main again:** keep AC #2 and the wire-versus-feed fix; stop a waiter that holds its own proof from falling through; re-decide only on health withdrawn, the model gone from the reported list, or a window that no longer fits; put the residual "successor claims after my proof" window in SPEC §15 naming TASK-25 AC #5 and TASK-64 AC #3 as owners — that row must say the narrowing leaves this open deliberately, not that the earlier behaviour was sound. **Commit 2:** AC #6's launch-sequence pin, and the fairness pin re-armed on an event rather than a sleep.

Do not read the resume check as the entitlement: authority held across the handoff is TASK-25's criterion.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Half-green at [042e212](https://github.com/ArborealML/Inference-Router/commit/042e212) and main has stayed red since.** CI run 44241484309 failed `SwitchFairnessTests.AWaiterBehindAnotherProfilesSwitchTakesItsOwnTurn` (assert at `SwitchFairnessTests.cs:113`, ~20 s = that caller's ceiling); the board reproduced it 2 of 4 runs of the two-test filter, and separately failed **this row's own pin** `AWaiterOvertakenAtTheResumeRead` 1 of 2 runs of the `SwitchClaimTests` filter at `SwitchClaimTests.cs:377`, whose diagnostic reads `launches=[extra,m,extra,m]` with a switch still in flight at 25 s. The mechanism and the rule are decision-36. The sleep at `SwitchFairnessTests.cs:109` is the sensitivity, not the defect; the seam AC #4 used is sound (`AfterReadyWaitForTests` at `EngineRuntime.cs:765`, before the resume read at `:782`) — what fails is the pin's premise, since its two callers are a ping-pong by construction.

**No fix on the remote** — re-measured 05:53Z: main is `2d2d9f4`; every push since the red commit is board-only and red on the same two tests (runs 44245983576, 44246239378: Failed 2, Passed 190, board-contract job green, so that red is carried, not a board regression). The fix-forward exists here only as uncommitted edits to `EngineRuntime.cs`, the two test files and `SPEC.md`. **Ordering:** TASK-62's category work comes after this row is green; TASK-25 AC #5 and TASK-64 AC #3 own the parked half.
<!-- SECTION:NOTES:END -->
