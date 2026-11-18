---
id: TASK-67
title: >-
  Settle the switch before its hint, make the claim decision atomic, and pin the
  claim's failure edges
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T00:53:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - concurrency
  - scheduling
milestone: m-2
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
- [x] #5 an unsuperseded switch still returns Ready and nudges dispatch once, pinned outside the gate; the raced-second-launch case is closed by the version CAS and recorded as an accepted gap in SPEC rather than pinned
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Complete. The one thing this row owed after its code landed — the SPEC §15 accepted-gap line — shipped in TASK-62's commit rather than its own, so the wire wording and the gap wording moved together.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Code landed at a228c6e (v0.12.32) on top of d31b35b (v0.12.31), CI green.** The failure was not the newcomer waiter: the in-flight caller woke on its readiness task resolving false and was told switch_failed, and a gate disposed under a live transaction threw into that switch's own reason. #1–#4 are pinned in `SwitchClaimTests`; revert runs put the newest hunk at 6 of 6 failing with the answer guard removed and 4 of 4 with the gate disposal restored. **Not covered by the mutation work:** removing settle-before-cancel leaves 8 of 8 passing, so that ordering is construction, not a regression-caught hunk.

**#5 closed at [3dce284](https://github.com/ArborealML/Inference-Router/commit/3dce284)**: SPEC §15 now records the raced second launch as an accepted gap and says plainly that the lost compare-exchange has no pin — `ASecondCallerTakesTheProofInsteadOfClaimingASecondSwitch` parks the second caller *before* it takes `_switchLock`, so it proves the claimant re-reads authority rather than losing the exchange. Closing that pin needs a seam inside the claim's await-free gate region, more production surface than a race the code excludes structurally is worth. TASK-68 AC #7 puts the same region under a new kind of pressure: once gauges bump the version, a lost claim becomes a normal outcome rather than a rare one, and decision-34 bounds the re-decides instead of pinning the loss.
<!-- SECTION:NOTES:END -->
