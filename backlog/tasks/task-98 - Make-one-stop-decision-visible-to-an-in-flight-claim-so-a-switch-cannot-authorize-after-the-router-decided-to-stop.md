---
id: TASK-98
title: >-
  Make one stop decision visible to an in-flight claim, so a switch cannot
  authorize after the router decided to stop
status: Testing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T11:02:00Z'
updated_date: '2026-11-18T19:37:00Z'
labels: []
milestone: m-2
dependencies: []
ordinal: 97000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Verify the post-stop claim mutation against the integrated shutdown code. The production changes and marker readback are on main.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The stop cause is written once and never rewritten, and the cancellation token source is not re-armed after a stop
- [x] #2 A claim that passed the switch gate before the stop sees the stop: it is released with a cause, and the cause it reports cannot change afterwards
- [ ] #3 A named test fails when the stop cause is re-armed or overwritten, and a second named test fails when the claim path ignores it
- [x] #4 SPEC says which cause wins when a stop and a switch race, in §5.5, in the same commit as the code
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
Stop publication and the SPEC §5.5 first-outcome rule landed together in [380a2a4](https://github.com/ArborealML/Inference-Router/commit/380a2a4). The timeout-precedence fix landed in [86f7bd6](https://github.com/ArborealML/Inference-Router/commit/86f7bd6). Review confirmed own-outcome precedence and stop-before-timeout for a foreign wait. The split-window fixture landed in [07408e2](https://github.com/ArborealML/Inference-Router/commit/07408e2). Its marker-only regression turned red against the old getter. The focused Release set passed 69/69. The late-overwrite mutation failed at RouterStopped versus Failed. Verify the distinct mutation removing the post-stop claim check before closing AC #3. TASK-81 owns the wire mapping.
<!-- SECTION:NOTES:END -->
