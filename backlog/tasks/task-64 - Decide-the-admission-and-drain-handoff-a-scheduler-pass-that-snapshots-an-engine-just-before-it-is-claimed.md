---
id: TASK-64
title: >-
  Decide the admission and drain handoff: a scheduler pass that snapshots an
  engine just before it is claimed
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-19T06:49:00Z'
labels:
  - scheduling
  - architecture
milestone: m-4
dependencies: []
priority: high
ordinal: 64000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Recorded as an accepted gap in SPEC 15 by the fix set for fd5e0cd; this row is the work that gap names. A scheduler pass snapshots an engine as healthy and idle; a switch then claims that engine under the transition gate; the pass dispatches a no-switch ticket to it without WaitForModel, so the request runs against an engine about to be stopped. Nothing here can patch it: correctness needs the admission/drain design EngineLifecycleCoordinator.cs:8-12 already describes and defers, where a drain marks the engine unavailable at admission and re-queues what already arrived. The same shape is documented there for the no-capacity case, so this row exists so the gap has a home rather than a comment. Acceptance is a shape, not a patch: what an in-flight pass is told when an engine is claimed under it, and what happens to a ticket already snapshot-approved.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 15 carries the accepted gap with this task named and the fd5e0cd review as its source
- [x] #2 the admission/drain shape is written as a decision with the in-flight-pass rule stated
- [ ] #3 a test shows a ticket approved by an earlier pass is not dispatched to an engine claimed after that pass
- [ ] #4 Consume completion and profile wakes while the route is closed. The same ticket stays queued with zero grants or bytes. Opening the matching generation wakes dispatch outside locks and sends that ticket once without another external event.
- [ ] #5 Hold the queue expiry callback behind the router lock past the original monotonic deadline. The final admission check refuses the expired ticket with zero upstream writes.
- [ ] #6 Close the ranked winner after scanning the first eligible ticket beyond its group. That ticket dispatches exactly once in baseline order; repeated wakes neither spin nor restart the closed preparation.
- [ ] #7 On a model-only route, use identical prompts and equal derived requirements. Withdrawing the measured window before exact take blocks only the explicit floor. Losing the final permit blocks either ticket. Refused tickets write zero bytes.
- [ ] #8 A submitted ordinary request with failed cleanup still consumes its physical slot after logical completion. A successor cannot write on that slot, and the cleanup fault remains observable.
- [ ] #9 Two routes sharing a single engine slot honor the eligible reopening owner. Competing owners or ordinary requests cannot acquire that slot simultaneously. Confirmed release permits progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Carry the final ranked ticket and its route generation through preparation. Release the router lock after selection. At first-write authorization take host admission, router and queue locks in that order. Recheck the exact ticket, model-bound capacity, free permits, thread room and original deadline. An explicit minimum context survives independently of tier inference. Count the physical use before releasing the guard and retain it through cleanup. Closed generations advance or park. Preserve the first candidate beyond the ranked group and publish reopen or capacity changes before waking dispatch outside locks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The shared-engine capacity correction at ac8b118 passed 149 focused Release checks. A submitted request with failed cleanup fences the successor after logical completion, an ordinary invocation consumes one of two permits, and staged owners share the final permit across routes. Public teardown guarantees safe captured-token access. Hosted CI and the dispatched correction review on [PR #5](https://github.com/ArborealML/Inference-Router/pull/5) wake integration. Production host admission remains separate.
<!-- SECTION:NOTES:END -->
