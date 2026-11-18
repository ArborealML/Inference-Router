---
id: TASK-72
title: >-
  Drain the waiting queue by age-weighted value before switching, without
  ping-pong
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T02:55:00Z'
labels: []
dependencies:
  - TASK-69
  - TASK-25
  - TASK-51
priority: high
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner's example, taken literally: one request waiting on the loaded model and eight waiting on another model means the router serves all eight, then switches — with age weighting so an old request counts for more than a new one — and does not flip back after the first new arrival. This is the no-ping-pong rule from decision-25, and it is a queue and scheduler behaviour, not a lifecycle one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The owner's example is pinned from a stated start: one model resident, one waiter on it and eight on another, served eight-then-switch
- [ ] #2 Under steady competing load the router does not alternate switches between two models, pinned by a test that counts switches
- [ ] #3 Under arrivals that never stop, every waiting request keeps an age under a bound named in SPEC and switches per unit time stay bounded, with the refusal that enforces the bound on the feed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A goal-level audit asked for the drain and admission rows (TASK-25, TASK-64), the queue ceiling (TASK-51) and the claim-correctness rows (TASK-67, TASK-68) to settle before this one; the two taken as dependencies are the ones this row consumes numbers from. Its warning that counting a switching ticket as inflight deadlocks the drain is already TASK-25's premise, not a new finding. The old #3 said only that an old wait is bounded; bounded now means an age bound and a switch-rate bound under arrivals that never stop.
<!-- SECTION:NOTES:END -->
