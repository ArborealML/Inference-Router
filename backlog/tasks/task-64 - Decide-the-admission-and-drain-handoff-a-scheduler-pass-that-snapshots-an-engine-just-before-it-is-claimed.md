---
id: TASK-64
title: >-
  Decide the admission and drain handoff: a scheduler pass that snapshots an
  engine just before it is claimed
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-19T06:09:00Z'
labels:
  - scheduling
  - architecture
milestone: m-4
dependencies: []
priority: medium
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
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Carry the final ranked ticket and its route generation through preparation. Release the router lock after selection. At first-write authorization take host admission, router and queue locks in that order. Recheck the exact ticket, model-bound capacity, free permits, thread room and original deadline. An explicit minimum context survives independently of tier inference. Count the physical use before releasing the guard and retain it through cleanup. Closed generations advance or park. Preserve the first candidate beyond the ranked group and publish reopen or capacity changes before waking dispatch outside locks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The combined injected candidate passed 71 focused checks, then 16 targeted checks pinned dispatcher-driven replacement. Review identified a further case when unrelated preparations fill the table before the detached owner is recorded. Verify and fix bounded progress from the real dispatcher. Production owner promotion and late cleanup remain under TASK-25.
<!-- SECTION:NOTES:END -->
