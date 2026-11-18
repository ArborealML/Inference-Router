---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: In Progress
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



Five CI occurrences, all Expected settled / Actual cancelled: 44264855629 at b7ab43d, 44275637518 at e0dcd0e, 44277482601 at 64b9f94 (rerun green), 44280833645 on a board-only push, 44308059362 at 14a01e2 (rerun requested by this seat). Five of the last seven pushes: the gate cannot be trusted until this is fixed, so this row goes first. Coding seat, two sightings — at `8e6867d` plus the taxonomy patch, 2 red / 1 green, this pin in its 30 s timeout shape and `RequestPhaseTimeoutTests.ACallerThatHangsUpIsCancelledEvenIfTheEngineIgnoresTheCancellation`; at `14a01e2`, Failed 1 / Passed 221 with the **test name lost to a pipe**, unreproduced in four Debug, three Release and six switch-class runs. Their report; that untitled sighting is the failure mode criterion #6 ends. Mechanism and mutation controls live in the implementation plan, from the top-tier review; nothing here is measured by this seat beyond the CI logs it pulled. Standing rule while open: no new pins in the switch-claim or capability-publication classes — amendments are allowed, and the taxonomy commit's census is on TASK-94.
**Started by the coding seat.** Reproduction for criterion #1: 20 focused Release runs of the pin at `64b9f94` — a sha before the taxonomy commit, which separates this flake from that change — in a detached worktree off the main tree, one log per run, counts reported with sha and command. Two follow-ups for whoever lands this row: tightening `CapabilityPublicationTests.CategoryOf` to assert the caller's text whole is a new assertion in a closed class, so it waits for this row to close; and the selector backstop's pin must land in a class this rule does not close.
<!-- SECTION:NOTES:END -->
