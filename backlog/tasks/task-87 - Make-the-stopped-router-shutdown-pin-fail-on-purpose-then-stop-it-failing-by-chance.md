---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: Testing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T19:46:00Z'
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
Historical worker measurement on 64b9f94: 24-core host, 40 concurrent CPU-only busy loops. Two blocks of 20 serial runs of dotnet test -c Release --no-build --filter FullyQualifiedName~SwitchClaimTests yielded 9/20 and 11/20 failures of the shutdown pin. The pin alone failed 0/20 quiet runs. On 51922fe, 42/50 focused runs passed; seven hit the 30000 ms ceiling and one expected settled but saw cancelled. Seven CI runs also saw that wake assertion. AC #2 still needs direct evidence for both mechanisms. CI run 44380388711 timed out in Pause.Enter in the later late-provider fixture. The amended fixture runs shutdown separately and releases the barrier before joining it.

In [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2) the held-provider pin captures the stopped answer before release. Coordinator inspected worker logs: waking before terminal publication failed AStoppedAnswerArrivesWhileTheProviderIsStillHeld; allowing a late overwrite failed AProviderFailureCannotOverwriteThePublishedStop. Both expected RouterStopped and observed Failed. Reading the private state from Switching failed SwitchingReportsThePublishedMarkerBeforePrivateStateIsInstalled at True versus False. The restored worker Release report records 69/69 in the focused set. Independent commit review accepted the fixture; TASK-59 carries the pre-install hook exception control. Fixed-tree host measurements remain open. Separate focused and join cohorts, record per-run load, and reserve the host under decision-55. Keep new pins outside the switch-claim and capability-publication classes until measurement finishes.
<!-- SECTION:NOTES:END -->
