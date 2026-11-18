---
id: decision-30
title: >-
  A switch stop is one monotonic decision, and every surface reports the same
  cause
date: '2026-11-18T04:19:00Z'
status: accepted
updated_date: '2026-11-18T04:19:00Z'
---
## Context

An external review of the last two engine commits found, and this seat confirmed by
reading the code, that `EngineLifecycleCoordinator.DisposeAsync` sets the shutdown flag
before it cancels the switch token, while `EndSwitch` re-derives that flag from the token
on every settle. An interleaving between the two turns a stop into a switch failure on the
wire — `cancelled`/499 becomes `switch_failed`/503 — and can label an unrelated failed
switch as cancelled. Two more surfaces disagree with the same event: a **streaming** request
cancelled during shutdown gets keepalives and then a bare end of body, because the terminal
error frame is written only for a failed ticket; and `DelegatedExecProvider` catches the
switch token's cancellation together with its own wait timeout, so the operator's feed says
`timeout_after_…` for a switch that was stopped.

## Decision

A switch stop is decided once, by whoever observes it first, and its cause is frozen. The
stop latch is written once and read everywhere: `EndSwitch` never re-derives it, each
`SwitchState` is fresh per switch and inherits nothing, the lifecycle provider distinguishes
its own deadline from the switch token's cancellation, and a cancelled stream either ends with
the existing lowercase error body plus `[DONE]` or SPEC says in one sentence that the stream
simply ends and why that is safe for a client.

## Consequences

Providers gain one more lifecycle rule to honour. The streaming choice is wire-visible on an
already-open stream, so it is taken out loud in the commit that makes it, not left to a
reader. Teardown order is fixed at the same time: a caller arriving after stop begins is
refused rather than served by a disposed provider, and an in-flight switch transaction joins
before the provider is disposed. Implementation is TASK-81; the pins it names are the proof.

## Alternatives

Re-derive the flag at each read (rejected: that is the race). Let each surface infer its own
cause from whatever it happens to see (rejected: decision-27 exists precisely because the
wire and the feed must not disagree about one switch).
