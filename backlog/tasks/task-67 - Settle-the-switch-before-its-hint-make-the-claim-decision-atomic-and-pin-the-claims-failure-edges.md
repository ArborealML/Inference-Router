---
id: TASK-67
title: >-
  Settle the switch before its hint, make the claim decision atomic, and pin the
  claim's failure edges
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T00:53:00Z'
updated_date: '2026-11-18T00:53:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 67000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The plan review of the set-1 fix split the work: the resume-authorisation half waits on an owner decision, and this row carries the half that does not. Four defects in `Engines/EngineRuntime.cs`, all verified: `EndSwitch` runs the injected dispatch hint at :705 before settling the readiness task at :706, so a throwing callback escapes into the catch at :825-833 and rewrites a proven switch; `PublishSwitchStarted` (:245-253) re-reads the capability and discards the snapshot the readiness question was answered on, so a poll that proves the model between the check and the marker buys a second launch; `DisposeAsync` disposes the switch lock at :899, turning shutdown into `ObjectDisposedException` reported as `engine_unreachable`; and the claim hook at :591-593 runs inside the transition gate with nothing to undo a throw.
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
(a) `EndSwitch`: commit in the gate → settle `state.Ready` → the hint in its own `try`, its throw swallowed with a comment. Every `Emit` already precedes `EndSwitch`, so decision-7's feed-before-wake rule still holds. (b) Atomic claim: make the marker come from the same snapshot the `IsReadyFor` decision used — an expected-version CAS that reports whether it claimed — and re-decide from the fresh snapshot when it did not; a re-check inside the gate only narrows the race, because the providers publish outside the gate. (c) Stop disposing `_switchLock` (:899, same reasoning as the deliberately undisposed `_switchCts` at :894-896); give the two waiter waits a linked token whose CTS *is* disposed, keep the token read at :582 for the engine-owned transaction, and move the shutdown expectation from `engine_unreachable` to `cancelled`. (d) On a throwing hook: leave the gate, then `EndSwitch(newState,false)`, then rethrow — `Monitor` is reentrant, so rolling back inside the lock would run the settle and the hint under the gate. Seams: keep `AfterClaimForTests` wrapped, add `AfterEndCommit` (past the gate, exceptions swallowed), a pre-wait hook between the fast-path miss and `_switchLock.WaitAsync`, and an injected launcher at the scheduling boundary. The forced-CAS-retry pin is dropped: publishing from the claim hook does not make `PublishProof`'s first exchange fail, and no seam is worth its production surface for it. This row claims no window or profile correctness — the gauge epochs stay open on their own row.
<!-- SECTION:PLAN:END -->
