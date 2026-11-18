---
id: TASK-94
title: Name every refusal category, and stop a fixed mismatch reading as a timeout
status: Waiting
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
- [ ] #7 The deep commit review of 14a01e2 lands and each finding carries a disposition: fixed in a later commit, or written up as an accepted SPEC gap
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed at **14a01e2** (SPEC v0.12.42). Criteria ticked on a read of that commit, not on a run by this seat: the walk asserts 11 exits over eight names written out in the test rather than read from the source; a source-audit test covers every failed return; `FeedReason.SwitchNotStarted` sits deliberately outside `SwitchReason`; the lifecycle name won the naming call; `EndSwitch` is held in a `finally`; SPEC carries the phase-not-a-cause reading. Runs are the coding seat's report — 93/93 focused, 222/222 in four Debug and three Release runs, AOT silent, six mutation probes reverted byte-identically. **CI 44308059362 went red at this sha** on `SwitchClaimTests.AWaiterInsideTheReadinessWaitIsToldTheRouterStopped` (Expected settled / Actual cancelled): TASK-87's flake, not this change; a rerun was requested, and TASK-87 moves to the front of the queue. Wake: criterion #7, the deep commit review of 14a01e2 dispatched 12:52:59Z; this seat dispatches its own if nothing lands by the next landing. Frozen-class census for TASK-87's rule: two amended assertions in `SwitchClaimTests`, one category assertion plus the closed-set count 7 → 6 in `CapabilityPublicationTests`, no new methods in either. The naming call went to a lifecycle name, and the wait-ceiling path took the scoping arm — an expired wait carries no name, pinned as `AnExpiredWaitIsNotAnAttemptAndCarriesNoName`. `RouterEvent.cs:85` still calls every subscriber unguarded: the guard at a new emit site is criterion #6, the general fix is TASK-103.
<!-- SECTION:NOTES:END -->
