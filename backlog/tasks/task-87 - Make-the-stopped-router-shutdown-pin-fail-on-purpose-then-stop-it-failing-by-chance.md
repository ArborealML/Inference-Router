---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T10:13:00Z'
labels:
  - flake
  - tests
dependencies: []
priority: high
ordinal: 86000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The pin SwitchClaimTests.cs:230 AWaiterInsideTheReadinessWaitIsToldTheRouterStopped fails in two shapes: it has hit its 30 s Fact ceiling, and on CI four times it answered an assertion instead — Expected settled, Actual cancelled. Load-dependent, never reproduced on demand at will, and a red main costs the next real failure its reading.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A construction fails the pin in 10 or more of 20 focused Release runs against 64b9f94 — not the fixed tree — with the SHA, command, environment and counts in this row.
- [ ] #2 The mechanism is named here and explains that reproduction, not a hypothesis.
- [ ] #3 Afterwards: 50 focused Release runs and 3 full Release suite runs with no failure, plus the same construction run against the fix.
- [ ] #4 Two named mutation controls fail pins: removing shutdown's settlement of the readiness task, and letting a stop read as a broken engine so a later write overwrites the cause.
- [ ] #5 The route assertion is gone, and a provider that records cancellation but stays held pins liveness: the stopped answer arrives while held, and the marker clears after release.
- [ ] #6 Latches fail safe: the observation is captured, released before the assertions and released in a finally, and every wait is bounded, so a red run fails rather than parks.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Dispositioned by the top-tier plan review and corrected: dropping Assert.Equal("settled", wake) is right, because the seam reports the winner of Task.WaitAsync arbitration, not a call order, and that branch is no contract. Reverting EngineRuntime.cs:794-801 is the wrong mutation — it mutates the custom answer only when token cancellation wins, and may be unreachable once a stop is a single decision. Worked as one change with [TASK-81], whose frozen stop cause removes the race from production; this row proves the pin is deterministic and that liveness survives it.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Four CI occurrences, all Expected settled / Actual cancelled: 44264855629 at b7ab43d (1/194), 44275637518 at e0dcd0e (1/199), 44277482601 attempt 1 at 64b9f94 (1/206, rerun green), 44280833645 on a board-only push (1/206). Four of the last five pushes. Coding seat reports 2 of 8 Release switch-filter runs failing at 64b9f94, the pin alone 6 of 6 — their report, not this seat's. Standing rule while open: no new pins in the switch-claim or capability-publication classes until this one is deterministic. Mechanism, as named by the plan review and consistent with this seat's read of the two sites: first-writer arbitration inside the cancellation promise of Task.WaitAsync, amplified by RunContinuationsAsynchronously — source completion may queue while cancellation runs inline, so call order does not decide which branch the seam reports. The 30 s shape is cleanup failure: an assertion inside the hook parks it. The reviewer read no code running; nothing here is measured by this seat beyond the CI logs it pulled.
<!-- SECTION:NOTES:END -->
