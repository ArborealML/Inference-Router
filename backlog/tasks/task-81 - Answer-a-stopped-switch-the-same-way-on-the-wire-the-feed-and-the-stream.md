---
id: TASK-81
title: Answer a stopped switch the same way on the wire, the feed, and the stream
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T04:09:00Z'
updated_date: '2026-11-20T23:55:00Z'
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
Independent review passed the queued HTTP/1.1 shutdown fixture plan. Frames owns the one-test implementation after the admin-switch handoff. The clean empty suffix is a prediction pending execution. The [active-read case](https://github.com/ArborealML/Inference-Router/pull/61) has separate landed evidence.
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
Add one Fact in StreamFrameIdentityTests. Hold the only slot and the clock. Using a real HTTP/1.1 client, read the exact first queued comment and capture its ticket. Start CopyToAsync directly on the remaining body and prove it is pending before stopping only the core. Propagate read faults; require clean EOF with zero tail bytes, zero forwards for the subject, and exactly one enqueue followed by one shutdown cancellation for the same ticket. Bound cleanup. Delivery runs the Release build and the single named case. This fixture records existing behavior; broader wire policy remains open.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decision-30 requires one monotonic cause across surfaces. TASK-98 owns publication order and TASK-87 measures the shutdown fixture. The plan review identified competing stream terminal writers: cancellation after zero or more deltas, provider completion during cancellation, and cleanup appending a sentinel without a terminal body. The TASK-108 review adds an upstream sentinel followed by a transport fault. Assert byte-for-byte that each path has one terminal owner, no later delta, and the contracted sentinel count across arbitrary chunk boundaries. Settle the error representation under AC #3 with a strict client; emitting both an error and a normal finish gives conflicting outcomes.
<!-- SECTION:NOTES:END -->
