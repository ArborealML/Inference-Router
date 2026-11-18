---
id: TASK-81
title: Answer a stopped switch the same way on the wire, the feed, and the stream
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-18T10:14:00Z'
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
A shutdown can be answered as a failure, read in the tree. EndSwitch derives `CancelledByShutdown` from the switch token (`EngineRuntime.cs:1054` at `2b751ec`) unconditionally, while teardown sets the stop flag and clears Ready before cancelling that token — so a switch that reads the flag just before the cancel and writes just after it overwrites true with false: a waiter due cancelled/499 gets switch_failed/503 and the feed says the switch failed. The reverse labels an unrelated failure as cancelled. Two gaps ride the path: a stream a shutdown cancels ends bare — the terminal frame and DONE are written only for a failed ticket; and DelegatedExecProvider folds its own wait deadline with the token cancellation, so the feed reads timeout_after_ms for a switch that was stopped. One deliverable: the stop decision is made once, monotonic, reported the same way everywhere.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The stop cause is recorded once and is monotonic: no later write can turn a shutdown cancellation into a switch failure or the reverse, and no switch state inherits a predecessor's flag.
- [ ] #2 A test that blocks a provider between the claim's cancellation check and its settle answers cancelled with the guard and fails without it, named here.
- [ ] #3 A streaming request that a shutdown cancels ends with an explicit terminal in the existing lowercase error shape, or SPEC says in one sentence that the stream simply ends and why that is safe for a client.
- [ ] #4 A stopped switch never appears on the feed as a timeout: the provider distinguishes its own wait deadline from the switch token's cancellation, with the reason on the event.
- [ ] #5 A caller that enters SwitchAsync after stop begins, on an unrelated token, is refused rather than served by a disposed provider, and an in-flight transaction joins before the provider is disposed.
- [ ] #6 A stopped switch is told apart from a slow one on the wire: the cancelled answer names no model, and the answer does not change across auto_switch and wait_health settings.
- [ ] #7 The cause-overwrite mutation fails a pin: dropping the stop decision, or letting a later write relabel it, breaks a named test.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One frozen stop result, read by `EndSwitch` instead of re-derived from the token: the readiness source carries the terminal state and the first **published** outcome wins (TASK-98 carries the gate ordering). Answer with a typed cause mapped to a lowercase wire code, never `OperationCanceledException`. §6.1 already answers a router-initiated cancel as `cancelled` at 499, so naming shutdown `router_stopped`, another router action `router_cancelled` and a client hang-up `client_cancelled` replaces a shipped code: a decision entry and a feed-string audit of the pins land in the same push. `cancelled_during_exec` stays a provider diagnostic.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
One change with [TASK-87](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-87%20-%20Make-the-stopped-router-shutdown-pin-fail-on-purpose-then-stop-it-failing-by-chance.md): this row removes the race from production, that row proves the pin stopped betting on it. Anchored on decision-30: one monotonic stop decision, every surface the same cause. The review ran nothing.

**From the stop-cause plan review (REVISE), the stream half.** `TicketState.Cancelled` does not own stream termination: provider completion can race cancellation into two terminal frames, a `finally` can write `[DONE]` with no terminal body, and cancellation can follow non-empty deltas. Require one atomic per-stream terminal owner and byte-level pins over: zero or more deltas, exactly one terminal representation, no delta after it, `[DONE]` only per contract. `finish_reason: "cancelled"` may break strict clients; an error envelope plus a normal finish gives one stream two meanings — settle against #3.
<!-- SECTION:NOTES:END -->
