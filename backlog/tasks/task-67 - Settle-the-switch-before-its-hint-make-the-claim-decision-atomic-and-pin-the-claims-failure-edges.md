---
id: TASK-67
title: >-
  Settle the switch before its hint, make the claim decision atomic, and pin the
  claim's failure edges
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T00:53:00Z'
updated_date: '2026-11-18T03:05:00Z'
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
- [ ] #1 the readiness task is settled before any injected callback runs, and a failing hint can neither rewrite a completed switch nor strand a waiter
- [ ] #2 the claim decision and the switch marker are published from one capability snapshot, so a poll that proves the model cannot be raced into a second launch
- [ ] #3 a synchronous scheduling failure at the launch boundary and a shutdown during a claim both settle the readiness task and clear the marker, and shutdown ends cancelled rather than engine_unreachable
- [ ] #4 a claim hook that throws rolls the claim back after leaving the transition gate, never while holding it
- [ ] #5 two callers of one already-proved model cause exactly one launch, pinned with no dependence on thread scheduling, and an unsuperseded switch still returns Ready and nudges dispatch once
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
(a) EndSwitch: commit in the gate, then settle state.Ready, then the hint in its own try with its throw swallowed and a comment. Every Emit already precedes EndSwitch, so decision-7's feed-before-wake rule still holds. (b) Atomic claim: the marker comes from the same snapshot the readiness answer was taken on — an expected-version CAS — and a lost exchange means re-deciding on the fresh snapshot, because the health loop and every switch exit publish outside the gate. (c) _switchLock is never disposed, as _switchCts is not; the two waiter waits carry a token linked to the engine's switch token, and that linked source is disposed with the call; shutdown answers cancelled, not engine_unreachable. (d) A throwing claim hook: leave the gate, then EndSwitch(claim,false), then rethrow — the gate is reentrant, so rolling back inside it runs the settle and the nudge while later claims queue behind it. The forced-CAS-retry pin is dropped: no seam is worth its production surface. Gauge epochs are TASK-68.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Main is red and the failure is inside this row.** SwitchClaimTests.ShutdownDuringAClaimEndsCancelledNotEngineUnreachable fails in 3 focused Release runs here, failed in CI at c4a8828 and 6863b9a, and **passed** at 9c8c57d, so it flakes rather than sitting cleanly red. Undisputed: the waiter parked before the switch lock completes normally instead of throwing OperationCanceledException, so the shutdown release the commit describes does not happen on that path; SemaphoreSlim.WaitAsync does throw for an already-cancelled token, so that token is not reaching the wait.

Order of work: reproduce, establish which path that waiter takes, fix so shutdown releases it as cancellation, and make the pin deterministic by construction — a gate, not a sleep — because a run that passes proves it can flake. No criterion is checked until the suite is green at HEAD. The full-suite pass reported before the push did not reproduce at HEAD: measure at the commit that lands, in Release, which is what CI tests. The red gate also holds TASK-28, TASK-42 and the dependent pin TASK-59.
<!-- SECTION:NOTES:END -->
