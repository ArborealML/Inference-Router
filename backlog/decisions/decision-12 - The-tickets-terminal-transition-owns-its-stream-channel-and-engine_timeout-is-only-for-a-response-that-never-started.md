---
id: decision-12
title: "The ticket's terminal transition owns its stream channel, and engine_timeout is only for a response that never started"
date: '2026-11-17T06:30:00Z'
status: accepted
---
## Context

Two things were ordered wrongly. The read pump closed a ticket's stream channel when the engine's response
ended, rather than when the ticket itself became terminal — so a stream whose engine sent headers and then
no frame simply stopped, with no error frame and no terminal event. And the response ceiling reported
`engine_timeout` as a 504 without regard to whether a response had already begun, which for a streaming
request is a lie: the `200` and whatever frames were already written are committed and unretractable.
Fixed in `76374d6` (SPEC §5.5, §6, §14.1; revlog v0.12.5).

## Decision

Options considered:

1. **Close the channel where the upstream response ends** — simple, and it leaves the caller holding a
   stream that never received its reason.
2. **Let the ticket's terminal transition close the channel**, so every terminal path (completion,
   failure, cancellation, timeout) writes its outcome and only then ends the stream.
3. **Give the timeout its own out-of-band channel** — rejected: the caller already has one stream, and a
   second is a new contract to explain.

**Chosen: 2**, with a rule about what a 504 may mean: **`engine_timeout` 504 exists only where no response
has started.** Once the `200` is committed, a ceiling expiry surfaces *in band* — one error frame followed
by `[DONE]`.

## Consequences

- **A 504 always means nothing was emitted, so it is safe to retry.** A caller that received an in-band
  error frame may already hold part of an answer and must treat it as partial.
- Every terminal path has to write its outcome before ending the stream — an ordering obligation per path,
  pinned by tests rather than by a comment (same shape as decision-7).
- The selector's own failure reason stays on the event feed (`engine.switch_failed`) and out of the
  caller-facing message: the caller sees the outcome, the operator sees the cause. Accepted as a gap in
  SPEC §15; it changes when §13 needs to distinguish the two programmatically.
- A phase-timeout value above `Int32.MaxValue` milliseconds fails at config binding rather than through
  the router's own validation message — startup still fails with a non-zero exit, only with a less
  specific line. Accepted gap, SPEC §15.

**Corollary pinned in v0.12.6.** The response ceiling is passed before any terminal work, so a failure inside the router's own completion path cannot be relabelled `engine_timeout`, and stream acquisition sits inside the ceiling.
