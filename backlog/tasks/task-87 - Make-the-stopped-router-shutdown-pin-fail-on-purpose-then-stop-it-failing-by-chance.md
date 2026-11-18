---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T09:56:00Z'
labels:
  - flake
  - tests
dependencies: []
priority: high
ordinal: 86000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Second flaky pin, separate from TASK-80's nudge case: SwitchClaimTests.cs:228 AWaiterInsideTheReadinessWaitIsToldTheRouterStopped fails in two shapes. It has timed out at its 30 s Fact ceiling, and twice on CI it answered an assertion instead — Expected settled, Actual cancelled. Implementer's counts at 6ea83ca: 1 failure in the first 8 combined-filter runs after the two new pins, 0 in the 16 after; 0 in 10 such runs at f89fead; once in four runs of the combined switch filter; it also timed out once under a source mutation and passed alone. Load-dependent, never reproduced on demand — and a red main costs the next real failure its reading.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A construction fails the pin in 10 or more of 20 focused Release runs, with counts in this row.
- [ ] #2 The mechanism is named here and explains that reproduction, not a hypothesis.
- [ ] #3 Afterwards: 50 focused Release runs and 3 full Release suite runs with no failure.
- [ ] #4 The pin still fails when the shutdown hunk it protects is reverted.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Plan of record, not started; moves to the plan field when work opens. Proposed by the coding seat, held for disposition until the top-tier plan review lands. Stop asserting which side of the wake race the seam records. Keep pinned what the caller gets: an OperationCanceledException naming the router stopping, no target named in the body, the provider observing the cancel, the marker cleared. Satisfy AC #4 by reverting the hunk at EngineRuntime.cs:794-801 — the fold of the engine switch token into the settled-false decision — because that revert changes the caller answer, so the pin fails for a reason that is not a race.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Live on main, three CI occurrences, all Expected settled / Actual cancelled: run 44264855629 at b7ab43d (1 of 194), 44275637518 at e0dcd0e (1 of 199), 44277482601 attempt 1 at 64b9f94 (1 of 206). The rerun of that last commit was green, so the pin is the variable, not the tree. Three of the last four pushes to main.

Standing rule while this row is open: no new pins land in the switch-claim or capability-publication classes until this pin is deterministic. And an assertion failure inside this test's hook leaves the hook parked, so a red run here can hang rather than fail — the 30 s shape.

Mechanism, read by the coding seat and consistent with this seat's read of the same lines: the hook argument at SwitchClaimTests.cs:241-246 records the winner of two releases of one wait — EngineRuntime.cs:781, where shutdown settled the readiness task, versus :794, where the engine switch token cancelled the wait while the caller token was not. Shutdown does both inside one window and the waiter does not resume synchronously with the settle, so the recorded branch is scheduler-dependent while v0.12.35 folds both into one caller answer. Their Release counts at 64b9f94: 2 of 8 switch-filter runs failed, one of each shape; the pin alone 6 of 6 — their report, not this seat's.
<!-- SECTION:NOTES:END -->
