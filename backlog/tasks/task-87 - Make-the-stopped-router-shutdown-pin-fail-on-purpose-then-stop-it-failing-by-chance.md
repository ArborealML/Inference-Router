---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: Testing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T19:38:00Z'
labels:
  - flake
  - tests
milestone: m-2
dependencies:
  - TASK-98
priority: high
ordinal: 86000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The shutdown fixture is integrated. Reconstruct both historical failure paths, then measure the reviewed fixed tree under the required quiet and loaded host conditions.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A construction fails the pin in 10 or more of 20 focused Release runs against 64b9f94 — not the fixed tree — with the SHA, command, environment and counts in this row.
- [ ] #2 The mechanism is named here and explains both observed shapes — the 30 s ceiling under load and the settled/cancelled assertion in CI — not a hypothesis
- [ ] #3 Afterwards, on a quiet host with load recorded: 50 focused and 3 full Release runs with no failure, plus the same construction against the fix.
- [x] #4 Two named mutation controls fail pins: a barrier that cancels or settles readiness **before** the terminal outcome is published, red at `router_stopped`, and a stop read as a broken engine so a later write overwrites the cause.
- [x] #5 The route assertion is gone, and a provider that records cancellation but stays held pins liveness: the stopped answer arrives while held, and the marker clears after release.
- [x] #6 Latches fail safe: the observation is captured, released before the assertions and released in a finally, and every wait is bounded, so a red run fails rather than parks.
- [ ] #7 A second quiet-host cohort runs `SwitchJoinTests` on the same tree with load recorded: it parks launches and exercises the changed join timing.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Dropping Assert.Equal("settled", wake) is right: the seam reports the winner of Task.WaitAsync arbitration, not a call order, and that branch is no contract. Reverting the readiness-wait block is the wrong mutation — it changes the answer only when token cancellation wins, and may be unreachable once a stop is one decision. Lands with TASK-98, whose frozen stop cause removes the race from production.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Historical baseline at 64b9f94: the old pin was green alone in 20 quiet runs and red 20 times in 40 loaded runs, including 11 failures in one block of 20. Seven CI runs observed the settled/cancelled mismatch. At 51922fe the 50-run focused cohort had seven timeouts and one route mismatch. Those counts precede the fix. AC #2 still requires direct evidence explaining the timeout stack as well as the assertion. CI run 44380388711 independently timed out in the later late-provider fixture at Pause.Enter. The new fixture runs shutdown on a separate task and releases its barrier before joining it.

The fixture landed in [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2). Both stop-order controls failed at RouterStopped versus Failed. The old Switching getter failed at True versus False between marker publication and private state installation. The restored focused Release set passed 69/69. Commit review dispatched. Keep the focused and join cohorts separate. Hold the measured host for each cohort under decision-55. Keep new pins outside the switch-claim and capability-publication classes until measurement finishes.
<!-- SECTION:NOTES:END -->
