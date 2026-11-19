---
id: TASK-64
title: >-
  Decide the admission and drain handoff: a scheduler pass that snapshots an
  engine just before it is claimed
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-19T07:42:00Z'
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
Independent plan review of the single completion/open fixture wakes implementation. The injected admission path landed in PR #5. Production host integration consumes this evidence separately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 15 carries the accepted gap with this task named and the fd5e0cd review as its source
- [x] #2 the admission/drain shape is written as a decision with the in-flight-pass rule stated
- [x] #3 a test shows a ticket approved by an earlier pass is not dispatched to an engine claimed after that pass
- [ ] #4 Consume completion and profile wakes while the route is closed. The same ticket stays queued with zero grants or bytes. Opening the matching generation wakes dispatch outside locks and sends that ticket once without another external event.
- [x] #5 Hold the queue expiry callback behind the router lock past the original monotonic deadline. The final admission check refuses the expired ticket with zero upstream writes.
- [x] #6 Close the ranked winner after scanning the first eligible ticket beyond its group. That ticket dispatches exactly once in baseline order; repeated wakes neither spin nor restart the closed preparation.
- [x] #7 On a model-only route, use identical prompts and equal derived requirements. Withdrawing the measured window before exact take blocks only the explicit floor. Losing the final permit blocks either ticket. Refused tickets write zero bytes.
- [x] #8 A submitted ordinary request with failed cleanup still consumes its physical slot after logical completion. A successor cannot write on that slot, and the cleanup fault remains observable.
- [x] #9 Two routes sharing a single engine slot honor the eligible reopening owner. Competing owners or ordinary requests cannot acquire that slot simultaneously. Confirmed release permits progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use public Router.Start with the startup health probe held. Prepare one real ticket, close its generation, and observe an initially parked dispatcher. Deliver switch completion and profile separately through the event ring. After each, require a distinct rescan and repark of the same prepared ticket with zero grants, calls and bytes. Publish matching Open once. Require the original ticket, stamp and exact body once, one terminal and one release. Isolate startup, preparation and timer wakes. Reuse existing observers, injected sink and existing closure, owner and retirement controls.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At ac8b118, 149 focused checks covered retained cleanup, engine-wide final-slot reservation, two-slot progress and exact take after close, expiry or window changes. [PR #5](https://github.com/ArborealML/Inference-Router/pull/5) merged at 1061d70 after green hosted CI. Review of 374af08 verified inline selection precedes execution scheduling and the held backend registers cancellation before signalling entry. Both orderings produce one terminal and return engine inflight to zero. Release cardinality, thread count at quiescence and every pre-send window are outside those assertions. Revisit when the release ordering or event delivery changes.
<!-- SECTION:NOTES:END -->
