---
id: TASK-81
title: Answer a stopped switch the same way on the wire, the feed, and the stream
status: To Do
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-20T22:58:00Z'
labels:
  - lifecycle
  - shutdown
  - wire
milestone: m-2
dependencies: []
ordinal: 81000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The queued HTTP audit predicts that a client which has received a queued comment sees clean EOF with no further bytes when the core stops. The ticket should record one cancellation. Existing tests separately cover queued cancellation and HTTP keepalives, and [the landed active-read terminal](https://github.com/ArborealML/Inference-Router/pull/61) passed [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44825528652). The combined queued HTTP case remains untested. A reserved fixture plan will hold the only slot and the clock, observe the first queued comment, stop only the core with the client reading, and check the exact suffix, zero upstream forwards and one correlated enqueue followed by one cancellation. The queued wire contract remains open.
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
Decision-30 requires one monotonic cause across surfaces. TASK-98 owns publication order and TASK-87 measures the shutdown fixture. The plan review identified competing stream terminal writers: cancellation after zero or more deltas, provider completion during cancellation, and cleanup appending a sentinel without a terminal body. The TASK-108 review adds an upstream sentinel followed by a transport fault. Assert byte-for-byte that each path has one terminal owner, no later delta, and the contracted sentinel count across arbitrary chunk boundaries. Settle the error representation under AC #3 with a strict client; emitting both an error and a normal finish gives conflicting outcomes.
<!-- SECTION:NOTES:END -->
