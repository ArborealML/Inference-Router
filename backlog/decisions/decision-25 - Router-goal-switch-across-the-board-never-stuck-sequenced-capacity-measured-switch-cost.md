---
id: decision-25
title: >-
  Router goal: switch across the board, never stuck, sequenced capacity, measured
  switch cost
date: '2026-11-18T01:20:00Z'
status: accepted
---
## Context

The router has been built and tested as if model switching were a rare, mostly opt-in act: one engine on this box,
one model loaded at a time, and the hard cases (a switch that fails partway, a queue that drains while an engine is
being claimed, the cost of loading a model at all) written down as gaps rather than as the normal path. The owner
has now stated the intended operating shape, and it is the opposite: this machine runs many models but never more
than one at a time, so its capacity comes from *sequencing* models across a fixed number of request lanes, and the
router has to do that sequencing itself, continuously, without ever getting stuck.

## Decision

Settled direction for the work, from the owner:

- **Switching is wanted across the board**, not as a per-engine opt-in.
- **A failed model swap must never leave the router stuck.** It keeps serving the requests it *can* serve; a
  refusal is a decision, not a side effect of a half-finished transaction.
- **No ping-pong.** With one request waiting on the loaded model and eight waiting on another, the router serves
  all of the second model's work, then switches to the first — with age-based weighting so an old request counts
  for more than a new one.
- **Switch cost is measured, not assumed.** Activation and shutdown duration per model per engine are tracked
  automatically, and those statistics are used to minimise context-switching and cache-eviction overhead subject
  to a hard rule that **no request waits forever**.
- **Capacity here is sequencing, not concurrency**: several models are runnable on this box but never concurrently.
- **Later, a cluster** of several machines, and a **local proxy mode in the `air` CLI** that receives client
  requests and forwards them to the real router, so that when the backend is down a local agent waits and proxies
  rather than failing.
- **Hard constraint to design against:** four concurrent request lanes for the whole box, one engine today.

## Consequences

Switch cost becomes a first-class quantity in the domain model, which means a measured per-model, per-engine
history and a scheduler that reads it — so the admission and drain handoff that SPEC has carried as an accepted
gap stops being a corner case and becomes the centre of the design. The posture questions this board had parked
as policy — whether a failed health proof stops serving a model, and what a caller is told when a switch is
superseded — can no longer be deferred, because the answer is now load-bearing for behaviour the owner wants on by
default. A design pass at the top reasoning tier precedes code, and the proxy mode is a new surface with its own
lifecycle and failure story rather than a flag on the existing server.
