---
id: TASK-114
title: Start an owned cold engine for a queued model when its host can switch
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T18:21:00Z'
updated_date: '2026-11-22T01:15:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-110
ordinal: 112000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for shared-host enforcement in TASK-110 to qualify cross-process and restart fencing. The original queued request already drives cold dispatch in the shipped local group. See the [published live verification](../../docs/local-model-switching.md#verification).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A queued request for a stopped owned target can trigger one native launch on an eligible host and dispatches only after target readiness is proven.
- [ ] #2 A failed probe alone grants no launch authority. Unknown ownership, unavailable lifecycle control or a failed host reservation starts nothing.
- [ ] #3 A competing request joins or waits under the host reservation, and healthy models on other hosts remain available.
- [ ] #4 A cold launch holds the host fence through verified incumbent release and target readiness. Race two target requests across a restart: at most one resident starts, and stale or uncertain occupancy permits neither launch nor dispatch.
- [ ] #5 Fail activation of A after incumbent stop with B waiting: each affected A ticket terminates once, B serves verified inference under a fresh transaction within its bound, and A remains latched (decision-62).
<!-- AC:END -->
