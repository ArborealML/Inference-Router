---
id: TASK-192
title: >-
  Decide an elective slot-tier raise from measured saturation with a payback
  bound
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T01:06:00Z'
updated_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-183
  - TASK-190
  - TASK-197
  - TASK-198
  - TASK-195
  - TASK-216
priority: high
ordinal: 184000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
AIR independently selects context, physical lanes and admission target for actual active/enqueued eligible work. Fit is hard; required expansion differs from elective resizing. Optimize finite-work service, not perpetual saturation; priority only selects competing pending work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Compare same cohort using measured full/partial wave times at exact recipe, context, physical lanes and occupancy; include unavoidable drain, transition and cache loss.
- [ ] #2 Compare singleton, underfilled, burst, full, mixed-context and staggered workloads to fixed baseline. Missing comparable evidence retains feasible incumbent.
- [ ] #3 At unchanged context vary admission/physical lanes when beneficial; at unchanged lanes expand and contract context where qualified.
- [ ] #4 Admission-only changes never restart or claim physical memory/speed recovery; lowering target permits natural drain.
- [ ] #5 Required fitting expansion obeys bounded wait/activation; elective physical changes require conservative benefit, hysteresis, cooldown and outage bounds.
- [ ] #6 Committed transition closes only conflicting admissions, preserves streams and refuses unsafe drain/identity; unrelated domains progress.
- [ ] #7 Select cohort by existing pending order/eligibility before optimization; no priority-dependent throughput, context, spending or lanes.
- [ ] #8 SPEC §§5, 7.6, 10, 14 replace contraction non-goal, record thresholds/uncertainty and report actual/desired/available capacity. Untested recipes never qualify.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use adopted finite table and simple count-based waves. Start with admission target; physical changes reuse TASK-190/197/198. Require service floors and measured benefit; mixed/staggered decisions need representative replay, not exhaustive benchmarking.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Preserve TASK-189 no-op, TASK-195 identity and TASK-196 explanation. Supersedes upsize-only policy.
<!-- SECTION:NOTES:END -->
