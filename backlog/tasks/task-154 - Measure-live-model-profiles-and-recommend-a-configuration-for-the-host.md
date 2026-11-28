---
id: TASK-154
title: Measure live model profiles and recommend a configuration for the host
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T01:17:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-170
  - TASK-171
priority: high
ordinal: 149000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qualify an authorized real host/model comparison using the delivered runner and search. This is the measurement and adoption-input experiment, not a second CLI implementation. Preserve the 262144 singleton TTFT target and compare exact physical recipes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Exclusive real measurements compare at least two feasible exact recipes, recording window, physical lanes, occupied concurrency and hardware identity.
- [ ] #2 Compare identical rendered work at singleton and full occupancy, plus a material partial point when cap exceeds two. Record transition and cache conditions.
- [ ] #3 Report median end-to-end completed-output throughput, TTFT, decode, completion and resource evidence; long-decode and near-window results remain separate.
- [ ] #4 Publish passing alternatives, failures, unsupported and untested points; identify missing evidence. Operator adoption is separate.
- [ ] #5 Run the authorized Hy4 reference where available or record the approved equivalent; pin host/recipe, exclusive load, cold transition and cleanup evidence before publishing a real recommendation.
- [ ] #6 Before enabling mixed-context or staggered adaptation, run authorized representative real replays, comparing matched work, transitions, settlement and service floors; retain exact evidence hashes per qualified mode.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TASK-170/171. Compare the same requests, not different near-ceiling prompts. Hold the authorized exclusive measurement scope (decision-55), preserve active sessions, prove settlement and retain all evidence. Actual reference/hardware and adoption are release gates, not prerequisites to offline runner/search/adoption code. Assign this measurement workstream the real mode replays; unsupported modes stay disabled without blocking homogeneous or fixed-placement delivery.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Existing planner/compare shipped; no new real measurement asserted. TASK-216 owns adoption.
<!-- SECTION:NOTES:END -->
