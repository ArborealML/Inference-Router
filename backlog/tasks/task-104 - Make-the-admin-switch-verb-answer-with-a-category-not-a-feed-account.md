---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T12:59:00Z'
updated_date: '2026-11-20T21:43:00Z'
labels: []
milestone: m-7
dependencies:
  - TASK-148
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
[Decision 79](../decisions/decision-79%20-%20Return-a-queryable-ID-for-an-explicit-admin-switch.md) selects an asynchronous acknowledgment and queryable operation ID for an explicit admin switch. Frames is revising the plan after wire review. Protect each accepted ID through settlement and the server’s acknowledgment send, then allow at least 15 minutes for lookup in the running router. Reserve capacity before acceptance and return a retryable 503 if protected records fill the store. Automatic switches do not consume these slots. Delivery will review the revised plan. It covers looking up the status, final outcome and failure reason for that request. The queue normally initiates switches.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 An accepted explicit admin switch returns 202 with an operation ID that identifies its status and terminal result on lookup.
- [ ] #2 Real HTTP tests cover immediate refusal, accepted success and accepted failure. Lookup by the returned ID reports the outcome of that operation.
- [ ] #3 SPEC §5.5 and §6.1 say what an operator does with that answer, and §15 stops listing the gap as open
- [ ] #4 The refusal carries the reason as a field the client reads, not a token inside a sentence — a client must not have to parse prose to branch on it
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Opened by the board from the coding seat's finding at 14a01e2: their grep for this surface across the board returned nothing, and SPEC §15 carries the gap with no criteria attached. Their correction, accepted and on the record: TASK-65 is the commit-identity decision and does not cover this — the taxonomy commit cites TASK-77/87/94/103 only. Wire-adjacent, so its plan goes to the top reasoning tier before code. Criterion #4 came from the deep review of 14a01e2: the taxonomy answer today is a sentence with a parenthesised token at the end, which is text a client has to parse.
<!-- SECTION:NOTES:END -->
