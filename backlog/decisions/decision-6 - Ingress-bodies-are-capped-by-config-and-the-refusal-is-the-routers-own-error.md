---
id: decision-6
title: "Ingress bodies are capped by config, and the refusal is the router's own error"
date: '2026-11-17T05:48:00Z'
status: accepted
---
## Context

The router buffers a request body in order to forward it upstream as bytes, so an unread request was also
unbounded in what one caller could make the process hold. Before this there was no stated limit: the host
default, if any, answered instead of the router, which put an error shape the caller could not branch on
in the middle of the router's own contract. Shipped in v0.12.1 (`edadb75`); SPEC §6 and the §14.1 row 2
gate.

## Decision

Options considered:

1. **Say nothing** and let the host's payload limit answer — a status and body the router does not own.
2. **A router-owned cap**: `max_request_body_bytes` (16 MiB by default, refused below 1 KiB), checked
   against `Content-Length` as a fast path and again against the counted copy, so a caller that
   understates its length is still cut off.
3. **Stream without buffering** so no cap is needed — rejected: the forward path is byte-for-byte
   forwarding, and re-chunking an unread body to avoid the buffer changes what is promised upstream.

**Chosen: 2.** The refusal is the router's own error contract — **413 `request_body_too_large`**,
lowercase like every other error body on every surface — never whatever the host would have answered.

## Consequences

- One new config key with a documented default, and a second reason a request can fail before any engine
  sees it; the error is table-pinned in `ErrorContractTests` alongside the rest of §6.1.
- The cap is enforced twice (declared length, then counted bytes), so a lying `Content-Length` cannot buy
  extra memory.
- A caller that needs larger prompts is configured up, not worked around: the limit is a knob, not a wall.
- §14.1 row 2 records bounded bodies as closed on the request side; what remains open there is the
  lifecycle reservation a switch should hold instead of an inference slot (TASK-25).
