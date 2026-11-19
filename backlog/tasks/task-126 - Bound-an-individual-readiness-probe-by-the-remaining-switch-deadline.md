---
id: TASK-126
title: Bound an individual readiness probe by the remaining switch deadline
status: Waiting
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-19T10:03:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 124000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Lifecycle’s pinned host change with the typed publication result wakes Shutdown to integrate the enrolled engine path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hold a state probe and then a models probe separately past the remaining switch budget. Each attempt settles within the named bound and produces one terminal result.
- [ ] #2 A delayed successful reply after timeout cannot publish readiness or revive a retired attempt. A healthy alternate can still make progress.
- [ ] #3 Return an enrolled refusal through RunSwitchAsync after expiry or shutdown has won. The caller retains the original terminal cause and the feed records one terminal event, with no second capability change, completion, or dispatch wake.
- [ ] #4 Exercise successful guarded publication with hooks that verify both short locks are released. A joined waiter woken by shutdown or expiry receives its committed result only after the outcome event is delivered.
- [x] #5 Hold the committed outcome event and stop the engine with two joined waiters on the same tier. After event delivery only the waiter whose minimum fits the committed measured window receives Ready. Neither waiter can dispatch.
- [ ] #6 For explicit named and resolved-default enrolled tiers, test a fitting window on the verified instance and route in budget. Omitted or zero measured total slots refuse publication; positive measured total slots on that binding permit readiness.
- [ ] #7 At absolute deadline equality, present otherwise qualifying window and positive slot evidence. The guard refuses publication, completion and permits. In the in-budget success control the hook receives the committed proof outside both short locks under the retained reservation.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one monotonic readiness budget across state headers and body, model discovery, and poll delay. Separate observation from publication so a late or cancellation-ignoring handler cannot publish gauges or readiness after terminal settlement. Preserve shutdown precedence and the last completed mismatch diagnosis. Use bounded held handlers and a controllable clock to exercise each wait, cancellation races and alternate recovery. Coordinate with TASK-68 on snapshot ownership.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Legacy readiness and local HTTP cleanup landed in [PR #4](https://github.com/ArborealML/Inference-Router/pull/4). The two-ticket control in [PR #8](https://github.com/ArborealML/Inference-Router/pull/8) passed both rows: the stopped engine granted neither ticket a send, and the open engine accepted one exact POST. For the offline enrolled case, Lifecycle owns coordinator, staging and the single recovered-launch fixture. Shutdown owns EngineRuntime and its host lifecycle partial. Build on the exact host child, reuse its launch-owned deadline and the existing guarded CAS and delivery signal, and consume the typed terminal outcome before generic launch failure. Preserve production capacity gates.
<!-- SECTION:NOTES:END -->
