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
- [x] #1 A construction fails the pin in 10 or more of 20 focused Release runs against 64b9f94 — not the fixed tree — with the SHA, command, environment and counts in this row.
- [ ] #2 The mechanism is named here and explains both observed shapes — the 30 s ceiling under load and the settled/cancelled assertion in CI — not a hypothesis
- [ ] #3 Afterwards, on a quiet host with load recorded: 50 focused and 3 full Release runs with no failure, plus the same construction against the fix.
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
Six CI occurrences, all Expected settled / Actual cancelled: 44264855629 b7ab43d, 44275637518 e0dcd0e, 44277482601 64b9f94 (rerun green), 44280833645 board-only push, 44308059362 14a01e2 (rerun requested), 44325281339 2727ba2 (board-only push). Six of the last eleven pushes: the gate is not a gate.

Criterion #1 is met on the coding seat's report, which came with sha, command and per-run logs: a detached worktree at `64b9f94`, `dotnet test --no-build -c Release`. The pin alone on a quiet machine: **0 of 20 red**. The whole `SwitchClaimTests` class with 40 busy loops on a 24-core host: **9 of 20**, repeated at **11 of 20**, 20 of 40 overall, and every red run named that test — zero unnamed reds. Their own caveat is the next question: under load the failure is the **30 s Fact ceiling**, not the settled/cancelled assertion, and CI shows the assertion. Criterion #2 has to answer both, which is why its wording changed.

That earlier sighting (`Failed 1 / Passed 221`, name lost to a pipe, clean in 13 further runs) is the failure mode criterion #6 ends. Their load hogs poisoned two local suite runs on this machine until killed; any local count from inside that window, mine included, is unusable. Standing rule while open: no new pins in the switch-claim or capability-publication classes — amendments are allowed, and the taxonomy commit's census is on TASK-94. The selector-backstop follow-up resolved at 2f7e3e6: the in-switch check was deleted as unreachable, and the blank-selector pin sits in the taxonomy class, which this rule does not close.
<!-- SECTION:NOTES:END -->
