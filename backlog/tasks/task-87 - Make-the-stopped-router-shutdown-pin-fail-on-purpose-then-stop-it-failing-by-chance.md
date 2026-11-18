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
The pin `AWaiterInsideTheReadinessWaitIsToldTheRouterStopped` fails in two shapes: it has hit its 30 s Fact ceiling, and seven CI runs answered an assertion instead — Expected settled, Actual cancelled. Load-dependent, never reproduced at will, and a red main costs the next real failure its reading.
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
Plan review disposition: dropping Assert.Equal("settled", wake) is right — the seam reports the winner of Task.WaitAsync arbitration, not a call order — and that branch is no contract. Reverting the readiness-wait block in EngineRuntime.cs is the wrong mutation: it changes the custom answer only when token cancellation wins, and may be unreachable once a stop is one decision. One change with TASK-81, whose frozen stop cause removes the race from production; this row proves the pin deterministic and liveness surviving.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Seven CI occurrences, all Expected settled / Actual cancelled: 44264855629 b7ab43d, 44275637518 e0dcd0e, 44277482601 64b9f94, 44280833645, 44308059362 14a01e2, 44325281339, 44330807651 dd22653 — board-only pushes among them.

Criterion #1 is met on the coding seat's report (sha, command, per-run logs): detached worktree at `64b9f94`, `dotnet test --no-build -c Release`. Pin alone on a quiet machine: **0 of 20 red**; the whole class with 40 busy loops on a 24-core host: **9 of 20**, repeated **11 of 20**, 20/40 overall, every red run naming that test.

The earlier sighting (`Failed 1 / Passed 221`, name lost to a pipe) is the failure mode #6 ends. Their load hogs poisoned two local suite runs; any count from that window, mine included, is unusable (decision-55). Standing rule while open: no new pins in the switch-claim or capability-publication classes (amendments allowed; the taxonomy class is not covered).

A quiet-host series is running on the post-#14 tree (`51922fe`, Release, `--no-build`): 50 focused runs of this class, then 3 full suites, load recorded per run, host 1.86 at the start with no load generator running (their report; raw counts land here). Runs taken before `51922fe` qualify for nothing at AC #3, while AC #1's `64b9f94` counts stand as history. The ordering that chose that tree sits in TASK-94: #14 first, because it changes what a caller inside the readiness wait does — and its parked-launch pins went into a new class, `SwitchJoinTests`, which kept the standing rule above.

AC #4-#6 unmet; the parked shutdown / stop-cause pins that would carry them are **unmeasured, not red** — their last numbers came from under their own 40 busy loops.
<!-- SECTION:NOTES:END -->
