---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T06:07:00Z'
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
- [x] #3 A waiter whose own attempt succeeded takes its own turn rather than a refusal when something other than a later claim contradicts the target, pinned where a second claim wins the engine
- [x] #4 The resume check is pinned by a gate between the readiness await and the snapshot read, so the pin cannot pass on thread scheduling
- [x] #5 A switch marker raised by a LATER claim never withdraws the proof a waiter's own switch published: a caller that holds its own proof is not sent behind another caller's transaction
- [ ] #6 Two callers wanting different models on a one-model engine produce no alternating launch sequence — pinned as a launch sequence, not as the absence of a timeout
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Fix-forward inside this row, two commits — 042e212 is not narrowed into the refusal work (that is TASK-62, shipped and reviewed at 3dce284). **Commit 1, green main again:** keep AC #2 and the wire-versus-feed fix; stop a waiter that holds its own proof from falling through; re-decide only on health withdrawn, the model gone from the reported list, or a window that no longer fits; put the residual "successor claims after my proof" window in SPEC §15 naming TASK-25 AC #5 and TASK-64 AC #3 as owners — that row must say the narrowing leaves this open deliberately, not that the earlier behaviour was sound. **Commit 2:** AC #6's launch-sequence pin, and the fairness pin re-armed on an event rather than a sleep.

Do not read the resume check as the entitlement: authority held across the handoff is TASK-25's criterion.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Green again at [f89fead](https://github.com/ArborealML/Inference-Router/commit/f89fead) (v0.12.35), commit 1 of the fix-forward.** Re-measured 06:05Z: `origin/main = f89fead`; CI run 44248248881 success on all three jobs after four red pushes. The pins resolve at HEAD: `OwnProofStillStands` in `EngineRuntime.cs`, `SwitchClaimTests.cs:397` for AC #5, and both resume pins take the seam at `EngineRuntime.cs:765` before the snapshot read at `:782` — the seam AC #4 needs, so neither pin passes on thread scheduling. The implementer reports the combined switch filter 11/11 over five consecutive runs plus a differential failing the new pin under v0.12.34's fall-through: their measurement, not the board's.

**Open: AC #6**, the two-caller launch-sequence pin, and the fairness pin re-armed on an arrival event instead of the `Task.Delay(150)` pair. Standing rules for that commit: the parked half stays parked — SPEC §15 names TASK-25 AC #5, TASK-64 AC #3 and this row as owners and says v0.12.34 is deliberately narrowed, so do not revert 042e212 and do not read the resume check as the entitlement (authority across the handoff is TASK-25's criterion, decision-36). TASK-62's AC #7-#10 come after this row is Done.
<!-- SECTION:NOTES:END -->
