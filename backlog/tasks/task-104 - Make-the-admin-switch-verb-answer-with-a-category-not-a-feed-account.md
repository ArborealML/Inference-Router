---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T12:59:00Z'
updated_date: '2026-11-20T22:58:00Z'
labels: []
milestone: m-7
dependencies:
  - TASK-148
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames owns the independently reviewed admin switch lookup in its dedicated implementation checkout on e8a1fb8. [Decision 79](../decisions/decision-79%20-%20Return-a-queryable-ID-for-an-explicit-admin-switch.md) keeps automatic switches queue-driven. An accepted explicit switch returns an ID for checking its outcome. Keep the result through settlement and acknowledgment, then allow at least 15 minutes for lookup within the running process.
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
Opened by the board from the coding seat's finding at 14a01e2: their grep for this surface across the board returned nothing, and SPEC §15 carries the gap with no criteria attached. Their correction, accepted and on the record: TASK-65 is the commit-identity decision and does not cover this — the taxonomy commit cites TASK-77/87/94/103 only. Wire-adjacent, so its plan goes to the top reasoning tier before code. Criterion #4 came from the deep review of 14a01e2: the taxonomy answer today is a sentence with a parenthesised token at the end, which is text a client has to parse.
<!-- SECTION:NOTES:END -->
