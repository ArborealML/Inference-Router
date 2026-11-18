---
id: decision-28
title: A retried submission is deduped in the forwarder, never by a new wire field
date: '2026-11-18T03:26:00Z'
status: accepted
---
## Context

A client that reconnects mid-request, and the local forwarder that waits and retries while the router
is unreachable, can both cause the same inference to run twice: nothing in the router identifies a
submission across connections, and nothing dedups. Fixing it at the API boundary would mean the caller
naming its submission on `/v1`, which is a wire change and the wire contract is not the router seat's
to move. The owner was given three options — honour a caller-supplied key on `/v1` with a bounded
dedup window; keep `/v1` untouched and dedup inside the forwarder; or accept the duplicate and document
it — and answered with one letter: **B**.

## Decision

Dedup lives in the local forwarder: it remembers what it has already forwarded and refuses to re-send
it. `/v1` gains no submission identity and no new header, and the router stays as it is.

The forwarder's memory is **in-process** for now. A forwarder that restarts mid-request can still
duplicate, and that limitation is stated in its documentation rather than papered over. Making the
memory durable is a later act with its own cost, not a hidden requirement of this one.

## Consequences

The duplicate-inference problem is closed for the case we create ourselves, and stays open for a client
that retries directly against the router; if a caller outside our control ever needs that, option (a)
comes back as a wire proposal with the decision trail already here. The forwarder row now has a retry
rule it can be written against, and the fleet queue inherits the same question later — a queue that
admits twice has the same defect at a different layer, and will be argued from this decision rather
than from scratch.

