---
id: decision-11
title: 'A model-switch transaction belongs to the engine, not to the request that triggered it'
date: '2026-11-17T06:30:00Z'
status: accepted
---
## Context

A switch is started on behalf of an admitted request, and until now it ran on that request's
cancellation token. So the transaction outlived its trigger but not its trigger's patience: a waiter that
gave up — caller disconnect, or its own `switch_wait_timeout_ms` ceiling expiring — could cancel the
switch mid-flight, and a switch aimed at *another* caller's model could latch a failure that then decided
this caller's fate. The deep review of the phase-timeout work found it; fixed in `76374d6` (SPEC §5.5,
§14.1, revlog v0.12.5).

## Decision

Options considered:

1. **Keep the switch tied to the initiating request** and accept that a caller who leaves can abort work
   other callers depend on.
2. **Give the switch a token the engine runtime owns**, cancelled only when the engine itself goes away
   (shutdown, engine removal), with each waiter holding only its own bounded interest in the outcome.
3. **Refuse to start a switch while any waiter might leave** — rejected: that makes switching impossible
   under exactly the load that needs it.

**Chosen: 2.** A model-switch transaction belongs to the engine. It runs on a token the engine runtime
owns; a caller's `switch_wait_timeout_ms` bounds only that caller's own wait.

## Consequences

- No waiter can cancel or latch a switch it does not own, and one caller's abandonment cannot poison the
  engine's state for everyone else.
- Waiters with different targets take their own turn rather than inheriting another caller's outcome;
  single-flight stays per engine (decision-9).
- The switch still finishes under its own command timeout, so an over-ceiling caller is released while the
  transaction continues — the ceiling is about the caller, never about the engine's state machine
  (decision-5).
- A switch is a long operation holding a slot while it drains the engine; giving it a reservation of its
  own rather than an inference slot is still open and carried as TASK-25.

**Extended in v0.12.6.** The same rule was extended: a waiter is never answered from a switch that is not its own target. A switch aimed at the same model under a different serving profile no longer answers a caller that asked for another profile, and a switch that failed for another target no longer fails it — the waiter takes its own turn (`SwitchFairnessTests`). The switch token is read before the transaction is scheduled and its source is deliberately not disposed, because an exception thrown before the switch task's own cleanup would strand every waiter on that engine.
