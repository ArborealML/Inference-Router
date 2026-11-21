---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: Done
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T12:59:00Z'
updated_date: '2026-11-21T00:18:00Z'
labels: []
milestone: m-7
dependencies:
  - TASK-148
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Explicit admin switches return a queryable operation ID. Automatic switches remain queue-driven. [PR #70](https://github.com/ArborealML/Inference-Router/pull/70) landed at a2b9cc1.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [x] #1 An accepted explicit admin switch returns 202 with an operation ID that identifies its status and terminal result on lookup.
- [x] #2 Real HTTP tests cover immediate refusal, accepted success and accepted failure. Lookup by the returned ID reports the outcome of that operation.
- [x] #3 SPEC §5.5 and §6.1 say what an operator does with that answer, and §15 stops listing the gap as open
- [x] #4 The refusal carries the reason as a field the client reads, not a token inside a sentence — a client must not have to parse prose to branch on it
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Preserve immediate refusal responses. Add OperationId to the accepted 202 response and serve GET /admin/switch/{operationId} with status and machine-readable reason. Reserve capacity before issuing work, track each accepted request independently through settlement and acknowledgment-send retirement, and exclude automatic switches from this store. Qualify real HTTP success, accepted failure, cancellation, joined requests and immediate refusal. Prove bounded capacity and retryable 503 before side effects. Exercise actual HTTP response completion, continued lookup while acknowledgment is held, and lookup just before and at expiry on the controlled clock. Update the operator guide, README and SPEC with the supported scope. Delivery owns focused qualification, independent commit review and hosted CI.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
All 29 focused cases passed. The integrated server passed hosted Release, Native AOT and smoke, including [post-merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44847590001).
<!-- SECTION:NOTES:END -->
