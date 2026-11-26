---
id: TASK-192
title: >-
  Decide an elective slot-tier raise from measured saturation with a payback
  bound
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T01:06:00Z'
updated_date: '2026-11-26T01:07:00Z'
labels: []
dependencies:
  - TASK-183
  - TASK-190
priority: high
ordinal: 184000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Demand above concurrency for a sustained period should raise the slot tier, but queue depth and dwell alone do not prove a benefit. A resize costs a container stop and cold start measured at about 110 s on this host, and the wider tier has to return enough extra throughput to pay for the lost service. This task carries the decision rule and its outage budget. The act of applying a decision, with its fencing and recovery, belongs to the engine-transition task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An upsize requires live unreserved compatible backlog with effective capacity saturated over a stated window; health, lifecycle and incompatible backlog are excluded.
- [ ] #2 SPEC states the dwell rule as a payback bound from measured disruption cost and observed throughput gain, with the numbers it was calibrated on.
- [ ] #3 No feasible upsize is a terminal decision for the affected demand, not a retry loop.
- [ ] #4 Elective resizes are capped per model and per host against an explicit outage budget, and a failed attempt's backoff survives a restart.
- [ ] #5 Desired, observed and admissible capacity are separate measurements on the status surface before the controller is enabled.
- [ ] #6 Tiers with different windows are not treated as capacity substitutes, and the no-window-shrink rule is preserved.
<!-- AC:END -->
