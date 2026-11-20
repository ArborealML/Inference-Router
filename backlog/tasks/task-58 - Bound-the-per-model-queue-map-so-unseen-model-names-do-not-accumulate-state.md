---
id: TASK-58
title: Bound the per-model queue map so unseen model names do not accumulate state
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T23:02:00Z'
labels:
  - scheduling
milestone: m-3
dependencies: []
priority: medium
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The read-only audit found that coherent admission refusal creates no queue, but conservatively admitted model names leave empty queue entries after their tickets depart. Reload preserves those entries. [Decision 80](../decisions/decision-80%20-%20Retire-empty-model-queues.md) selects exact empty-entry retirement. After handing off the tuning planner, Config began the plan for retiring empty queues.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 5.5 states the bound or eviction rule for distinct model queues, with the default named
- [ ] #2 a test shows N distinct model names cost a bounded amount of router state
- [ ] #3 the rule cannot evict or refuse a queue that holds a live ticket
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan a deterministic map-size check with a live sentinel, sequential distinct admitted names and a definite-refusal control. Cover membership while publication is pending, stale selection across same-name recreation, zero-row removal and ordering among equal-depth queues. Preserve admission and exact ticket settlement. Independent plan review precedes implementation.
<!-- SECTION:NOTES:END -->
