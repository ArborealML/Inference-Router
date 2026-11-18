---
id: TASK-98
title: >-
  Make one stop decision visible to an in-flight claim, so a switch cannot
  authorize after the router decided to stop
status: Testing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T11:02:00Z'
updated_date: '2026-11-18T19:02:00Z'
labels: []
dependencies: []
ordinal: 97000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fix the two remaining shutdown edges identified in the 380a2a4 review before starting the quiet-host cohort.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 The stop cause is written once and never rewritten, and the cancellation token source is not re-armed after a stop
- [ ] #2 A claim that passed the switch gate before the stop sees the stop: it is released with a cause, and the cause it reports cannot change afterwards
- [ ] #3 A named test fails when the stop cause is re-armed or overwritten, and a second named test fails when the claim path ignores it
- [ ] #4 SPEC says which cause wins when a stop and a switch race, in §5.5, in the same commit as the code
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two steps, after the top-tier plan review of the first plan (verdict REVISE).

STEP 1, one stop published under the gate: write the stop latch **inside `_transitionGate`**, capture the installed state, release the gate, then cancel the linked CTS and settle readiness. Publishing a cause before the cancel is not enough — `WaitAsync` can throw on cancellation before anything reads the terminal state, so the terminal outcome has to be published before the wake. The post-stop claim check is the first thing under that same gate, ahead of join/claim branching, catalog work, marker CAS and publication. `EndSwitch` sets the terminal outcome before it clears readiness or completes the switch, and the resume read takes an acquire read or a typed completion result.

STEP 2, one typed answer: never reply with `OperationCanceledException` — a mechanism, and it makes the answer depend on which await was reached. Catch cancellation at each stop-cancellable wait, translate it against the frozen cause into a typed `RouterStopped`, map it to the lowercase wire code `router_stopped`. Naming: `router_stopped` for shutdown, `router_cancelled` for another router action, `client_cancelled` for the client; `cancelled_during_exec` stays a provider diagnostic. Surfaces land with TASK-81.

First **published terminal outcome** wins: stop first means `router_stopped` and a late success is telemetry; served first means the served answer stands. If shutdown must dominate globally, serialize `EndSwitch` with shutdown and record that as a decision.

Cheaper form: if `Ready` is itself terminal, `TaskCompletionSource<SwitchEnd>.TrySetResult` publishes the cause and wakes waiters in one atomic act and drops the separate field; it does not settle global claim/stop ordering, or callers cancelled before they await.

Three deliverables, in sequence, not one commit: lifecycle semantics here; the delegated-exec/SSE terminal protocol on TASK-81; the deterministic fixture and its counts on TASK-87 — counts are evidence, not code.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
380a2a4 passed 53 focused tests and both stop-order mutation controls. Commit review verified first publication and the two cancellable waits. AnswerExpiredSwitchWait can still report timeout after an unrelated captured attempt finishes and shutdown wins before the deadline decision. The public Switching property still reads switch state outside the transition gate. Preserve earlier own outcomes, classify the expired foreign wait using the stopped state under the gate, and derive public switching from the scheduler marker. The shipped shutdown wire code remains cancelled/499 until TASK-81.
<!-- SECTION:NOTES:END -->
