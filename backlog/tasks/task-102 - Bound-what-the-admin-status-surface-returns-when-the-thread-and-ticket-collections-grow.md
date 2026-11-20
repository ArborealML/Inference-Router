---
id: TASK-102
title: >-
  Bound what the admin status surface returns when the thread and ticket
  collections grow
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T11:39:00Z'
updated_date: '2026-11-20T22:46:00Z'
labels: []
milestone: m-11
dependencies: []
ordinal: 101000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The status endpoint serializes retained threads, per-model queue depths, configured engines and their loaded models. Distinct short thread IDs can grow the response while inference is idle. Queue depth, ticket and event retention limits do not bound this endpoint. The audit on 4cd1e85 found no collection projection or overall response budget. Future design must specify retrieval beyond a limit, the scope of any byte budget, and handling of a single whole value that exceeds it. Preserve complete accepted identities.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 The number of thread records the status surface will report is stated, in config or as a named default, and enforced where the set is built
- [ ] #2 A pin asserts the response stays under a stated byte budget with the set at its limit, with no field truncated
- [ ] #3 SPEC says what an operator does past the limit — a filter, a page, or a second surface — rather than leaving it implicit
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Cut from the TASK-97 plan review, which said a per-identifier cap does not bound an unbounded collection and that paging needs its own design. Verified by this seat: no thread-count bound anywhere in `src/`. Depends on TASK-97, because the multiplication only becomes interesting once per-identity storage is bounded.
<!-- SECTION:NOTES:END -->
