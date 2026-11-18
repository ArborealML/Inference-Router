---
id: TASK-113
title: Simplify router locking after the pi-clm model-swap proof
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T18:15:00Z'
labels: []
dependencies:
  - TASK-112
ordinal: 111000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Begin after the usable router has passed the end-to-end pi-clm swap milestone. Map the request, queue, switch and event locks to the invariants they protect. Select simplifications using contention measurements from a representative workload.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record the baseline workload, lock wait and hold measurements, and the invariant protected by each lock proposed for removal or narrowing.
- [ ] #2 Remove redundant synchronization or shorten measured critical sections. Compare contention and request latency under the same workload before and after.
- [ ] #3 Verify admission, switch ownership, shutdown, cancellation and stream completion under concurrency. Repeat the pi-clm swap proof on the changed build in an authorized environment.
<!-- AC:END -->
