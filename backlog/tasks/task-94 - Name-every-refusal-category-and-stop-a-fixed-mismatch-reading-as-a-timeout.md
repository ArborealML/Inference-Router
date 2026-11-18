---
id: TASK-94
title: Name every refusal category, and stop a fixed mismatch reading as a timeout
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
labels:
  - wire
  - switch
dependencies: []
priority: medium
ordinal: 93000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Re-measured at 610f0d8. Two refusal exits still hand the caller free-form prose outside the closed set of seven: EngineRuntime.cs:620, where a remote engine loads its own models, and :728-729, where the switch task never started — the second interpolates the switch target into the caller's sentence. Separately, one token carries three causes: with the prove-health path on, an unhealthy probe, a `/models` answer omitting the target, and a window too small all arrive as one category; with it off, the last two get their own names. A caller cannot tell a fixed configuration mismatch from a transient timeout, which is the one distinction a retry decision needs. This is the taxonomy half of the refusal work; the size and leak half is the row before it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every refusal a caller can see carries one named category: the two free-form exits join the set or the set narrows to them, and a test walks every exit rather than a selection.
- [x] #2 A permanent configuration mismatch and a transient timeout are not called by one category name, or SPEC states the phase-only reading in one sentence and the caller's retry decision follows from it.
- [x] #3 SPEC 5.5 says what each category means to the caller — retry, retry elsewhere, or fix the configuration.
- [x] #4 The pin states the expected literals itself, asserts set equality against `SwitchReason`, and an audit accounts for every failed return so a tenth exit cannot pass silently
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The added switch event is emitted once, before waiters settle, with cleanup in `finally`, so a feed observer cannot wedge the claim marker
- [x] #7 The deep commit review of 14a01e2 lands and each finding carries a disposition: fixed in a later commit, or written up as an accepted SPEC gap
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed at **14a01e2** (SPEC v0.12.42); criteria ticked on a read of the commit, not a run by this seat. Runs are the coding seat's report — 93/93 focused, 222/222 in four Debug and three Release runs, AOT silent, six mutation probes reverted. **CI 44308059362 went red at that sha** on `SwitchClaimTests.AWaiterInsideTheReadinessWaitIsToldTheRouterStopped`: TASK-87's flake, not this change; a rerun was requested and TASK-87 went to the front of the queue.

**Deep commit review at that sha (extra-high): REVISE, seven findings; every code claim re-measured by this seat at `eba81d4` before recording. **Closed at 2f7e3e6 (SPEC v0.12.43)**, verified here: the false §5.5 arm replaced by a new version line saying the stop answers cancellation / 499 and naming `RouterCore`'s `OperationCanceledException` arm; `SwitchReason.All` is now a `ReadOnlySpan<string>` over a private array; the refusal helper asserts `Detail` whole; the blank-`select.exec` case is pinned before the claim and the unreachable in-switch check is deleted rather than kept. Routed findings sit on TASK-103 #4, TASK-68 #6 and TASK-104 #4. A deep review of 2f7e3e6 went out at the top tier at 13:51Z; its findings will be dispositioned on whichever row they touch.
<!-- SECTION:NOTES:END -->
