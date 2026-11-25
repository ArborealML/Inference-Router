---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: Preparing
assignee:
  - '@chatswarm'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-25T23:53:00Z'
labels: []
dependencies: []
priority: high
ordinal: 181000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live event feed records a switch to the profile 256k-8 at 17:40 on an engine that was already serving 256k-8 with a measured window of 262144 and 8 slots. The old container exited at that moment and a new one started; the switch took about two minutes, and every request in flight on the only healthy engine died with it. The same thing happened at 06:00 on this host, and at startup the router launched the model at the narrow-slot default tier and then immediately switched it to the wide-slot tier of the same window. A no-op target must be recognised before any lifecycle transaction begins.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A demand already satisfied by the resident tier never starts a switch.
- [ ] #2 A pin reddens when a switch names the tier the engine is already serving.
- [ ] #3 Startup does not launch the default tier and then immediately switch away from it when a wider tier of the same window satisfies demand.
- [ ] #4 Any switch that does start records the demand that required it in the feed with the target and the resident state.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Slice one, one feature branch. (1) Switch pre-flight: resolve the target model plus tier, compare against the resident verified serving snapshot (model, measured window, measured slots); when the resident already satisfies the demand, complete without a lifecycle transaction and emit no switch event. (2) Lifecycle stop guard: before any container stop or delete on the switch path, count in-flight leases on that engine; if non-zero and no explicit operator force, defer the resize, leave requests queued, and record the target, the resident state and the reason. (3) Startup launch tier: pick the tier from demand rather than the declared default so the router does not launch the narrow tier and immediately switch away. Pins to redden: a resident 262144-window eight-slot engine answers a 262144-token demand with zero container stops; a 256000-token demand is served by the same resident with no switch; a resize attempted with one in-flight stream is deferred and that stream completes; after startup no switch follows the initial launch when a wider tier of the same window fits. Inference-path refusal names stay out of this slice pending wire review.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A plan review covering both slices is dispatched to the external reasoning service (2026-11-25, top reasoning tier); the text under review is the Implementation Plan section of this task and of the stream-cause task it gates. Implementation starts in its own worktree when that review lands. Live measurements from this host: switch to the resident tier took 115.8s and 118.0s on two occurrences, each ending every in-flight stream; startup launched the narrow tier and switched 90 seconds later.
<!-- SECTION:NOTES:END -->
