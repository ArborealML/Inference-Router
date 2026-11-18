---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: In Progress
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T06:27:00Z'
updated_date: '2026-11-18T19:29:00Z'
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
The dedicated implementer is building the reviewed shutdown fixture against 380a2a4. Ordinary focused checks can run concurrently; the quiet-host measurement series requires an exclusive window.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A construction fails the pin in 10 or more of 20 focused Release runs against 64b9f94 — not the fixed tree — with the SHA, command, environment and counts in this row.
- [ ] #2 The mechanism is named here and explains both observed shapes — the 30 s ceiling under load and the settled/cancelled assertion in CI — not a hypothesis
- [ ] #3 Afterwards, on a quiet host with load recorded: 50 focused and 3 full Release runs with no failure, plus the same construction against the fix.
- [ ] #4 Two named mutation controls fail pins: a barrier that cancels or settles readiness **before** the terminal outcome is published, red at `router_stopped`, and a stop read as a broken engine so a later write overwrites the cause.
- [ ] #5 The route assertion is gone, and a provider that records cancellation but stays held pins liveness: the stopped answer arrives while held, and the marker clears after release.
- [ ] #6 Latches fail safe: the observation is captured, released before the assertions and released in a finally, and every wait is bounded, so a red run fails rather than parks.
- [ ] #7 A second quiet-host cohort runs `SwitchJoinTests` on the same tree with load recorded: it parks launches and exercises the changed join timing.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Dropping Assert.Equal("settled", wake) is right: the seam reports the winner of Task.WaitAsync arbitration, not a call order, and that branch is no contract. Reverting the readiness-wait block is the wrong mutation — it changes the answer only when token cancellation wins, and may be unreachable once a stop is one decision. Lands with TASK-98, whose frozen stop cause removes the race from production.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Seven CI occurrences (Expected settled / Actual cancelled): 44264855629 b7ab43d, 44275637518 e0dcd0e, 44277482601 64b9f94, 44280833645, 44308059362 14a01e2, 44325281339, 44330807651 dd22653

AC #1 is met on their report: at `64b9f94`, 0/20 red for the pin alone when quiet; 20/40 with 40 busy loops on a 24-core host, every red naming it.

**AC #3 is not ticked.** The `51922fe` series (Release, `--no-build`): 42/50 focused clean, 8 red, all this pin — 7 `30000 ms` timeouts, 1 `Expected: "settled"` — both CI shapes, no load generator; full suites 231/231, then 2 failures, then 231/231 — one was TASK-80's nudge pin, red once. Host 1.86 at the start, then 6.4-15.0 from the harness's own `testhost`/MSBuild. AC #1-class evidence, not an after-the-fix pass.

AC #7: `SwitchJoinTests` parks launches and runs two callers per engine under the new timing; three full suites have too little power, so it is its own quiet-host cohort. Cohorts never mix.

**From the stop-cause plan review (REVISE).** The switch-lock-wait mechanism is unproven as quoted: it needs `_switchLock.WaitAsync` to take the linked token and the seam to follow acquisition, so AC #2 stays open. Add `WaiterCancelledAtSwitchLockIsReportedRouterStopped`: readiness seam not reached, answer `router_stopped`. Dropping shutdown's settlement of readiness can stay green — CTS cancellation is another wake; #4's barrier mutant separates them.

Standing rule while open: no new pins in the switch-claim or capability-publication classes (amendments allowed); counts from a window with foreign load are void (decision-55). AC #4-#6 unmet; the parked stop-cause pins are **unmeasured, not red**. Next act: TASK-98's production change, then this row's fixture.
<!-- SECTION:NOTES:END -->
