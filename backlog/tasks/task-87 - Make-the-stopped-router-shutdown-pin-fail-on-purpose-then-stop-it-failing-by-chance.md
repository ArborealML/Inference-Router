---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: Testing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T20:39:00Z'
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
The coordinator reserved the shared host and started fixed-tree shutdown and join cohorts on 0287a52. Record load and every run result. Re-run affected counts if another seat adds load.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A construction fails the pin in 10 or more of 20 focused Release runs against 64b9f94 — not the fixed tree — with the SHA, command, environment and counts in this row.
- [x] #2 The mechanism is named here and explains both observed shapes — the 30 s ceiling under load and the settled/cancelled assertion in CI — not a hypothesis
- [ ] #3 Afterwards, on a quiet host with load recorded: 50 focused and 3 full Release runs with no failure, plus the same construction against the fix.
- [x] #4 Two named mutation controls fail pins: a barrier that cancels or settles readiness **before** the terminal outcome is published, red at `router_stopped`, and a stop read as a broken engine so a later write overwrites the cause.
- [x] #5 The route assertion is gone, and a provider that records cancellation but stays held pins liveness: the stopped answer arrives while held, and the marker clears after release.
- [x] #6 Latches fail safe: the observation is captured, released before the assertions and released in a finally, and every wait is bounded, so a red run fails rather than parks.
- [x] #7 A second quiet-host cohort runs `SwitchJoinTests` on the same tree with load recorded: it parks launches and exercises the changed join timing.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Dropping Assert.Equal("settled", wake) is right: the seam reports the winner of Task.WaitAsync arbitration, not a call order, and that branch is no contract. Reverting the readiness-wait block is the wrong mutation — it changes the answer only when token cancellation wins, and may be unreachable once a stop is one decision. Lands with TASK-98, whose frozen stop cause removes the race from production.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At 64b9f94 on a 24-core host with 40 CPU-only busy loops, two blocks of 20 serial dotnet test -c Release --no-build --filter FullyQualifiedName~SwitchClaimTests runs produced 9/20 and 11/20 shutdown-pin failures. The pin alone failed 0/20 quiet runs. At 51922fe, 42/50 focused runs passed; seven hit 30000 ms and one expected settled but observed cancelled.

On .NET 11.0.0 a diagnostic with byte-verified historical runtime and test sources reproduced three schedules. Readiness resumed on another thread and the disposer returned. Inline cancellation ran the hook on the disposing thread; the original release after DisposeAsync was unreachable. Queued cancellation let disposal return, then the settled/cancelled assertion threw before releasing the hook. The diagnostic bounded each wait and released the hook independently. Original timeout logs do not identify the schedule of each individual run.

The repaired [fixture](https://github.com/ArborealML/Inference-Router/commit/07408e2) captures the stopped answer before provider release. Early wake failed AStoppedAnswerArrivesWhileTheProviderIsStillHeld; late overwrite failed AProviderFailureCannotOverwriteThePublishedStop. Both expected RouterStopped and observed Failed. The private-state getter failed SwitchingReportsThePublishedMarkerBeforePrivateStateIsInstalled at True versus False. The restored worker report records 69/69 focused Release tests. Commit review accepted the fixture; TASK-59 owns pre-install hook exception coverage.

[Failed CI](https://github.com/ArborealML/Inference-Router/actions/runs/44380388711) at 8a7a937 timed out in Pause.Enter. [CI with the repair](https://github.com/ArborealML/Inference-Router/actions/runs/44382999975) passed. Reserve the host under decision-55, record per-run load and keep the join cohort separate. Keep new pins outside the switch-claim and capability-publication classes until measurement finishes.

At 0287a52 the reserved 24-core host completed 50/50 focused shutdown runs, 3/3 full Release suites, and a separate 50/50 SwitchJoinTests cohort. The one-minute load across 209 observations ranged from 1.23 to 2.96. The loaded historical construction is running separately on the same fixed tree.
<!-- SECTION:NOTES:END -->
