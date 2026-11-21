---
id: TASK-114
title: Start an owned cold engine for a queued model when its host can switch
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:21:00Z'
updated_date: '2026-11-21T02:46:16Z'
labels: []
milestone: m-4
dependencies:
  - TASK-110
ordinal: 112000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The offline installer and readiness stage are composed; 111 focused Release tests passed. The ordinary one-slot queue proof still requires the selected ticket transfer and verified first write. The guarded settlement records launch readiness once; opening uses that proof and checks the original ticket for cancellation or revocation. A one-slot target must permit that same ticket through once without a second charge. Coordinate the installer with the cohort dispatcher and closed-successor readiness checks. With one engine instance per model the inactive instance has no responding inference endpoint. At 60849d9 PrefixAwareScheduler.SelectNext discards an unhealthy engine before testing whether the native provider can launch it. Distinguish a proven stopped, owned target from a failed probe or an unreachable lifecycle provider.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A queued request for a stopped owned target can trigger one native launch on an eligible host and dispatches only after target readiness is proven.
- [ ] #2 A failed probe alone grants no launch authority. Unknown ownership, unavailable lifecycle control or a failed host reservation starts nothing.
- [ ] #3 A competing request joins or waits under the host reservation, and healthy models on other hosts remain available.
- [ ] #4 A cold launch holds the host fence through verified incumbent release and target readiness. Race two target requests across a restart: at most one resident starts, and stale or uncertain occupancy permits neither launch nor dispatch.
- [ ] #5 Fail activation of A after incumbent stop with B waiting: each affected A ticket terminates once, B serves verified inference under a fresh transaction within its bound, and A remains latched (decision-62).
<!-- AC:END -->
