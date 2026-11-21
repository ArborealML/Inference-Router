---
id: TASK-58
title: Bound the per-model queue map so unseen model names do not accumulate state
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T23:46:00Z'
labels:
  - scheduling
milestone: m-3
dependencies: []
priority: medium
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config picked up the independently reviewed empty-queue retirement implementation after the workload planner handoff. [Decision 80](../decisions/decision-80%20-%20Retire-empty-model-queues.md) governs removal of exact empty queue entries. Coordinate shared admission and revision-history changes before integration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 5.5 states the bound or eviction rule for distinct model queues, with the default named
- [ ] #2 a test shows N distinct model names cost a bounded amount of router state
- [ ] #3 the rule cannot evict or refuse a queue that holds a live ticket
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Declare and serve ready fake routes A, B and C. Verify that a stale dispatch pass ends before a separate scan, and that equal-depth survivors B1 and C1 stay ahead of recreated A2. Check the actual map reference separately: cancellation of the old ticket can independently produce Changed. Verify stats omit the retired queue while its pending sibling remains until its own removal. Preserve map-size, refusal, publication and ticket settlement controls. Revision .120 is provisional behind the workload planner and admin switch lookup.
<!-- SECTION:NOTES:END -->
