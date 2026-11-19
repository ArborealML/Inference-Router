---
id: TASK-64
title: >-
  Decide the admission and drain handoff: a scheduler pass that snapshots an
  engine just before it is claimed
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-19T08:30:00Z'
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
The scheduler admission and drain handoff is documented and qualified with injected exact-ticket and completion/open witnesses. Production host admission is assigned to the switch, host and instance-dispatch tasks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 SPEC 15 carries the accepted gap with this task named and the fd5e0cd review as its source
- [x] #2 the admission/drain shape is written as a decision with the in-flight-pass rule stated
- [x] #3 a test shows a ticket approved by an earlier pass is not dispatched to an engine claimed after that pass
- [x] #4 Consume completion and profile wakes while the route is closed. The same ticket stays queued with zero grants or bytes. Opening the matching generation wakes dispatch outside locks and sends that ticket once without another external event.
- [x] #5 Hold the queue expiry callback behind the router lock past the original monotonic deadline. The final admission check refuses the expired ticket with zero upstream writes.
- [x] #6 Close the ranked winner after scanning the first eligible ticket beyond its group. That ticket dispatches exactly once in baseline order; repeated wakes neither spin nor restart the closed preparation.
- [x] #7 On a model-only route, use identical prompts and equal derived requirements. Withdrawing the measured window before exact take blocks only the explicit floor. Losing the final permit blocks either ticket. Refused tickets write zero bytes.
- [x] #8 A submitted ordinary request with failed cleanup still consumes its physical slot after logical completion. A successor cannot write on that slot, and the cleanup fault remains observable.
- [x] #9 Two routes sharing a single engine slot honor the eligible reopening owner. Competing owners or ordinary requests cannot acquire that slot simultaneously. Confirmed release permits progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use public Router.Start once. Hold the boot probe and omit the queue timer. Prepare the ticket before closing its generation. Observe an incomplete parked waiter before each event. Inject completion then profile separately; each must complete the prior waiter and produce exactly one rescan of the same ticket and a distinct incomplete waiter. Check the original preparation under its gate, one preparation attempt, zero selections, submissions, physical uses, releases and bytes. Matching Open alone sends the original stamp and exact body. Hold the response after the sink receives it: one use, zero releases and no terminal. Release it, await terminal and physical release separately, then check totals. Clear hooks and release barriers in finally with bounded waits. Out-of-lock notification ordering is established by source inspection.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #5](https://github.com/ArborealML/Inference-Router/pull/5) landed injected admission. [PR #13](https://github.com/ArborealML/Inference-Router/pull/13) qualified the completion/profile wake sequence and exact matching open. [PR #15](https://github.com/ArborealML/Inference-Router/pull/15) merged at b6f2ecf after green CI and independent PASS on b45c876. SPEC section 15 cites the review of fd5e0cd and records the scope of both witnesses. The reviewer verified the wording and evidence scope; the historical recommendation was supplied by the task record. Production host activation and dispatcher teardown completion are outside these witnesses.
<!-- SECTION:NOTES:END -->
