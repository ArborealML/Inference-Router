---
id: decision-9
title: 'Single-flight is per engine, not per target'
date: '2026-11-17T05:48:00Z'
status: accepted
---
## Context

The lifecycle design serialises model switches so two transactions cannot fight over one backend. The
question is what "one" is keyed on: the model target a switch is aimed at, or the engine that will serve
it. On a shared daemon, two different models requested from the same engine still touch the same
container, the same ports and the same readiness state. Stated in SPEC §5.3.1 with the container hardening
in v0.12.2 (`18d7279`).

## Decision

Options considered:

1. **Per target** — switches aimed at different models may run at once.
2. **Per engine** — every switch on one engine serialises, whichever model it is aimed at.
3. **Per host/daemon** — one transaction for the whole machine.

**Chosen: 2.** Single-flight is **per engine**, not per target: two switch requests for different models on
one engine serialise, which is stricter than the target's identity but matches one daemon transaction per
engine. Option 3 was rejected as over-serialising a machine that may host several engines.

## Consequences

- No two transactions can hold contradictory views of one engine's readiness, and a failed switch cannot
  be raced by a second attempt aimed at a different model.
- Throughput cost is deliberate: a burst of requests for different models on one engine queues behind each
  other's switches. That is what the request-side ceilings (decision-5) bound on the caller's behalf.
- The engine's own lifecycle command still runs under its own `wait_timeout_ms`; a queued request waiting
  behind another request's switch is released at its own ceiling while the switch continues.
- What a switch *holds* while it runs — an inference slot versus a lifecycle reservation of its own — is
  still open and carried as TASK-25.
