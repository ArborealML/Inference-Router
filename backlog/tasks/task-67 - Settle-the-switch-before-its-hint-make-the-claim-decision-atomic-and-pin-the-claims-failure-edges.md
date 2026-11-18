---
id: TASK-67
title: >-
  Settle the switch before its hint, make the claim decision atomic, and pin the
  claim's failure edges
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T00:53:00Z'
updated_date: '2026-11-18T03:45:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 67000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four defects in the switch-claim path in Engines/EngineRuntime.cs, all real, found in the set-1 plan review: the dispatch hint ran before the readiness task was settled, so a throwing callback rewrote a switch that had already succeeded and stranded its waiters; the switch marker was published from a different capability snapshot than the readiness answer, so a health poll that proved the model bought a second launch; shutdown disposed the switch lock, so a clean shutdown read as engine_unreachable; and a claim hook that threw had nothing to undo its claim. The fix is v0.12.31 at d31b35b; the state of its pins is in Notes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the readiness task is settled before any injected callback runs, and a failing hint can neither rewrite a completed switch nor strand a waiter
- [x] #2 the claim decision and the switch marker are published from one capability snapshot, so a poll that proves the model cannot be raced into a second launch
- [x] #3 a synchronous scheduling failure at the launch boundary and a shutdown during a claim both settle the readiness task and clear the marker, and shutdown ends cancelled rather than engine_unreachable
- [x] #4 a claim hook that throws rolls the claim back after leaving the transition gate, never while holding it
- [ ] #5 an unsuperseded switch still returns Ready and nudges dispatch once, pinned outside the gate; the raced-second-launch case is closed by the version CAS and recorded as an accepted gap in SPEC rather than pinned
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
(a) EndSwitch: commit in the gate, then settle state.Ready, then the hint in its own try with its throw swallowed and a comment. Every Emit already precedes EndSwitch, so decision-7's feed-before-wake rule still holds. (b) Atomic claim: the marker comes from the same snapshot the readiness answer was taken on — an expected-version CAS — and a lost exchange means re-deciding on the fresh snapshot, because the health loop and every switch exit publish outside the gate. (c) _switchLock is never disposed, as _switchCts is not; the two waiter waits carry a token linked to the engine's switch token, and that linked source is disposed with the call; shutdown answers cancelled, not engine_unreachable. (d) A throwing claim hook: leave the gate, then EndSwitch(claim,false), then rethrow — the gate is reentrant, so rolling back inside it runs the settle and the nudge while later claims queue behind it. The forced-CAS-retry pin is dropped: no seam is worth its production surface. Gauge epochs are TASK-68.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Landed at a228c6e (v0.12.32) on top of d31b35b (v0.12.31): CI green, and 7 of 7 focused Release runs here pass.** The failure was not the newcomer waiter at all: the in-flight caller woke on its readiness task resolving false and was told switch_failed, while a lifecycle gate disposed under a live transaction put ObjectDisposedException into that switch's own reason. #1–#4 are checked: their pins exist in SwitchClaimTests and pass, and revert runs put the newest hunk at 6 of 6 failing with the answer guard removed and 4 of 4 with the gate disposal restored. **What the mutation work honestly does not cover:** removing settle-before-cancel leaves 8 of 8 passing, so that ordering is construction rather than a regression-caught hunk, and the row does not claim otherwise. #5 stays open on one thing only — the raced second launch is closed by the version CAS but has no pin, the seam having been rejected as too much production surface, so SPEC owes an accepted-gap line, in TASK-62's commit.
<!-- SECTION:NOTES:END -->
