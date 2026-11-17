---
id: decision-5
title: 'Request-side phase timeouts: two named ceilings, not one global deadline'
date: '2026-11-17T05:48:00Z'
status: accepted
---
## Context

A queued request could sit in `WaitingForModel` for as long as the engine took to change model, and an
engine that accepted a request and then went quiet held its slot until the caller gave up. The existing
queue deadline did not cover either wait: an expired queue deadline says nothing about a switch that has
already started or about a turn at the engine that never answers. Lifecycle phases were already timed
(`stop_timeout_ms`, `wait_timeout_ms`, `call_timeout_ms`, health `timeout_ms`), so the unbounded waits
were all on the request side. Shipped in v0.12.4 (`4b806da`); SPEC §5.5, §6 and the §14.1 row 2 gate.

## Decision

Options considered:

1. **One per-request deadline** covering the whole admission-to-first-byte span.
2. **One ceiling per wait that the queue deadline does not cover**: a scheduler
   `switch_wait_timeout_ms` (default `0` = no ceiling) bounding a queued request's wait for a model
   switch, and a per-engine `response_timeout_ms` (default `600000`, `0` disables) bounding one turn at
   that engine.
3. **Nothing** — trust the caller to disconnect.

**Chosen: 2.** Over the switch ceiling the request fails `switch_failed` with its capacity slot handed
back, and the switch itself runs on under the command's own `wait_timeout_ms`: the ceiling is about the
caller, not about the engine's state machine. Over the engine ceiling the request fails `engine_timeout`
(504) and the slot is released. The switch's wait is bounded per **request** rather than per slot, which
is what keeps a queued burst from inheriting one caller's patience.

## Consequences

- Two new config keys, documented in the shipped sample (`switch_wait_timeout_ms = 0`,
  `response_timeout_ms = 600000`), so the default behaviour is unchanged for anyone who does not set a
  switch ceiling and bounded for everyone else.
- A wedged engine process is still noticed through health probes, not through the request path; the
  request-side ceiling stops the *bleeding*, it does not diagnose the cause.
- The response timer must not share the caller's cancellation token: sharing it made a client abort
  complete the delay and get answered `engine_timeout` instead of `cancelled`. `RequestPhaseTimeoutTests`
  pins all three outcomes — timeout, switch failure, and that an abort still reports `cancelled`.
- §14.1 row 2 (transport discipline) is now closed **on the request side**; what stays open there is the
  lifecycle reservation a switch should hold instead of an inference slot, carried as TASK-25.
