---
id: TASK-98
title: >-
  Make one stop decision visible to an in-flight claim, so a switch cannot
  authorize after the router decided to stop
status: In Progress
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T11:02:00Z'
updated_date: '2026-11-18T18:18:00Z'
labels: []
dependencies: []
ordinal: 97000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The top-tier plan review for the flaky shutdown pin ([TASK-87](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-87%20-%20Make-the-stopped-router-shutdown-pin-fail-on-purpose-then-stop-it-failing-by-chance.md)) split the work: that row proves the *switch-cancellation* contract, and this row carries the *stop-decision* half. Today `SetStopCauseLocked` overwrites the cause and can re-arm the cancellation token source per generation, so a claim that read the switch decision before the stop keeps running against it, and a waiter can be released with one cause and reclassified by another. One stop decision, frozen, monotonic, and visible to an in-flight claim, makes both readings agree.
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
Found by the plan review dispatched for TASK-87 (top reasoning tier): the review withdrew the plan's proposed mechanism and named this production half instead. Evidence for the current behaviour is in that row's Notes. This row is the one that can fix the second flake shape — a waiter released as `cancelled` and then reclassified.
<!-- SECTION:NOTES:END -->
