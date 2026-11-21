---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T12:59:00Z'
updated_date: '2026-11-21T00:11:00Z'
labels: []
milestone: m-7
dependencies:
  - TASK-148
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The workload planner landed at 6597024 with green main CI, releasing the base for admin switch lookup integration. Wake on Stream Delivery’s exact integration, review and hosted checks. Under Decision 79, automatic switches remain queue-driven. Accepted explicit switches return queryable operation IDs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 An accepted explicit admin switch returns 202 with an operation ID that identifies its status and terminal result on lookup.
- [ ] #2 Real HTTP tests cover immediate refusal, accepted success and accepted failure. Lookup by the returned ID reports the outcome of that operation.
- [ ] #3 SPEC §5.5 and §6.1 say what an operator does with that answer, and §15 stops listing the gap as open
- [ ] #4 The refusal carries the reason as a field the client reads, not a token inside a sentence — a client must not have to parse prose to branch on it
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Preserve immediate refusal responses. Add OperationId to the accepted 202 response and serve GET /admin/switch/{operationId} with status and machine-readable reason. Reserve capacity before issuing work, track each accepted request independently through settlement and acknowledgment-send retirement, and exclude automatic switches from this store. Qualify real HTTP success, accepted failure, cancellation, joined requests and immediate refusal. Prove bounded capacity and retryable 503 before side effects. Exercise actual HTTP response completion, continued lookup while acknowledgment is held, and lookup just before and at expiry on the controlled clock. Update the operator guide, README and SPEC with the supported scope. Delivery owns focused qualification, independent commit review and hosted CI.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review passed the final held-write fixture correction on 89016dd. Release built with zero warnings/errors; six affected cases and all 29 broader focused cases passed. Unchanged server inputs retain the zero-warning AOT receipt. The missing-OperationId baseline and ten negative controls remain pinned to their original revisions. Delivery owns composition on the landed .118 base, integration review and exact hosted checks.
<!-- SECTION:NOTES:END -->
