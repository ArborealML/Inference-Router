---
id: TASK-81
title: Answer a stopped switch the same way on the wire, the feed, and the stream
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:09:00Z'
labels:
  - lifecycle
  - shutdown
  - wire
milestone: v0.2 — the switching backend
dependencies: []
ordinal: 81000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external review of `ab54d27..a228c6e` found, and this seat confirms by reading, that a shutdown can be answered as a failure. `EngineLifecycleCoordinator.DisposeAsync` sets the shutdown flag and clears `Ready` **before** it cancels the switch token, while `EndSwitch` writes `CancelledByShutdown = _switchCts.IsCancellationRequested` unconditionally. A switch that evaluates that expression just before the cancel and writes it just after the shutdown flag lands overwrites true with false: the waiter that was about to be told `cancelled`/499 is told `switch_failed`/503 instead, and the feed says the switch failed. The same interleaving can push the other way, labelling an unrelated failed switch as cancelled. Two honest-answer gaps ride the same path. A **streaming** request cancelled by shutdown gets keepalives and then a bare end of body: `ServerApp.StreamTicketAsync` emits the terminal error frame and `[DONE]` only for a failed ticket, so the client cannot tell a stopped router from a truncated answer. And `DelegatedExecProvider` catches the switch token's cancellation and its own wait timeout in one clause, so the operator's feed says `timeout_after_<wait>ms` for a switch that was stopped, not one that was slow. One deliverable: the stop decision is made once, is monotonic, and every surface reports the same cause.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The stop cause is recorded once and is monotonic: no later write can turn a shutdown cancellation into a switch failure or the reverse, and no switch state inherits a predecessor's flag.
A test that blocks a provider between the claim's cancellation check and its settle answers `cancelled` with the guard and fails without it, named here.
A streaming request that a shutdown cancels ends with an explicit terminal in the existing lowercase error shape, or SPEC says in one sentence that the stream simply ends and why that is safe for a client.
A stopped switch never appears on the feed as a timeout: the provider distinguishes its own wait deadline from the switch token's cancellation, with the reason on the event.
A caller that enters `SwitchAsync` after stop begins, on an unrelated token, is refused rather than served by a disposed provider, and an in-flight transaction joins before the provider is disposed.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Replace the boolean with a single monotonic stop latch and a frozen cause, written once by whoever observes the stop first, read by `EndSwitch` instead of re-deriving it from the token; `EndSwitch` keeps the "first writer wins" rule for everything else. Then make the provider classify its own wait deadline separately from the switch token — the `cancelled_during_launch` reason already exists in the test provider as a reason string, so the shape is known — and carry it onto the event. The SSE choice is a real fork: emit the existing error frame plus `[DONE]` for a cancelled ticket (one more body on an already-open stream, shape unchanged), or document the bare end as the contract; name it in the commit either way. The teardown order fix is small but needs a disposed check at the entry of `SwitchAsync` and a join before the provider is disposed; the runtime's initial `IsReadyFor` must not answer Ready after stop.
<!-- SECTION:PLAN:END -->
