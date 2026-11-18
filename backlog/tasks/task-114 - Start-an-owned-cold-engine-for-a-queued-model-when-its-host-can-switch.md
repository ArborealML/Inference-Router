---
id: TASK-114
title: Start an owned cold engine for a queued model when its host can switch
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:21:00Z'
updated_date: '2026-11-18T18:51:00Z'
labels: []
dependencies:
  - TASK-110
ordinal: 112000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
With one engine instance per model the inactive instance has no responding inference endpoint. At 60849d9 PrefixAwareScheduler.SelectNext discards an unhealthy engine before testing whether the native provider can launch it. Distinguish a proven stopped, owned target from a failed probe or an unreachable lifecycle provider.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A queued request for a stopped owned target can trigger one native launch on an eligible host and dispatches only after target readiness is proven.
- [ ] #2 A failed probe alone grants no launch authority. Unknown ownership, unavailable lifecycle control or a failed host reservation starts nothing.
- [ ] #3 A competing request joins or waits under the host reservation, and healthy models on other hosts remain available.
- [ ] #4 A cold launch holds the host fence through verified incumbent release and target readiness. Race two target requests across a restart: at most one resident starts, and stale or uncertain occupancy permits neither launch nor dispatch.
- [ ] #5 Fail activation of A after incumbent stop with B waiting: each affected A ticket terminates once, B serves verified inference under a fresh transaction within its bound, and A remains latched (decision-62).
<!-- AC:END -->
