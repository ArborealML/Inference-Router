---
id: TASK-102
title: >-
  Bound what the admin status surface returns when the thread and ticket
  collections grow
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:39:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels: []
milestone: m-10
dependencies: []
ordinal: 101000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Bounding every identifier still leaves `/admin/status` a surface whose size grows with the number of threads: no thread-count limit exists in config (`grep -rn "MaxThread|Threads.Count" src/` is empty) and the handler serialises the whole set. Once TASK-97 admits identities at 2048 bytes, a large thread set is a large response by multiplication rather than by one oversized value. This is a separate act — a limit on a collection or a paging rule — and it must not be solved by truncating fields.
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
