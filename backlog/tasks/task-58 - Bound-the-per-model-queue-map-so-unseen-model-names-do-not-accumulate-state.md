---
id: TASK-58
title: Bound the per-model queue map so unseen model names do not accumulate state
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-21T00:01:00Z'
labels:
  - scheduling
milestone: m-3
dependencies: []
priority: medium
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The verified workload planner and admin switch lookup landings wake integration of queue retirement. Independent commit review passed 08ee455. Delivery owns the merge preview, review of the integrated change, hosted checks and a separate merge-turn request.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 5.5 states the bound or eviction rule for distinct model queues, with the default named
- [ ] #2 a test shows N distinct model names cost a bounded amount of router state
- [ ] #3 the rule cannot evict or refuse a queue that holds a live ticket
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
All 37 selected Release cases passed with zero skips; allocation was excluded. Source review verified all five retirement paths hold the router lock then the queue lock and remove only the mapped queue at zero depth. The dispatch reference check precedes removal, permit acquisition and charging. The stale selection test verifies absence of old-pass effects and the B1, C1, A2 service order; it does not independently isolate the reference check. Pending publication and overflow with live tickets are covered.
<!-- SECTION:NOTES:END -->
