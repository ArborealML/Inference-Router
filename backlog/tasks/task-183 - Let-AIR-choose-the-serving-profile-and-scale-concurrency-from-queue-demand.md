---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:49:00Z'
updated_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-203
priority: high
ordinal: 175000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Separate request context fit from physical recipe and admission concurrency. Client names model and optional numeric minimum; AIR chooses recipe. Context must not imply a fixed or larger lane count. TASK-192 owns demand optimization.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A fitting verified resident serves without relaunch; a busy preferred route cannot hide other eligible ready alternatives.
- [ ] #2 Candidate requirement is max(client floor, rendered input plus enforced output/reasoning and backend reserve). Validate exact or qualified conservative counts and malformed/conflicting inputs.
- [ ] #3 Known legacy profile names migrate explicitly to numeric minima; ambiguous recipe semantics refuse. Retire header after client migration.
- [ ] #4 Report context, physical lanes, admission target, active/reserved and available capacity separately. Admission target does not reclaim fixed physical KV allocation.
- [ ] #5 Busy compatible resident does not force context escalation; compare eligible alternatives or wait under independent policy.
- [ ] #6 Exact launch conformance remains recipe-bound for AIR-selected targets; numeric suitability never grants unqualified lanes.
- [ ] #7 SPEC §§5–7, 10, 14 pin fit, migration and separate dimensions; preserve resident no-op and physical ownership.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Land ingress normalization and shared candidate fit independently of elective resize. Count by backend/render identity; count/tokenize egress obeys policy. No silent truncation. Keep active launch unchanged until conformance and adopted evidence allow transitions. Remove automatic greater-slot tie-break.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Previous review confirmed name matching and declaration-order tie-break. Revision 4 supersedes wider-lane preference and contraction non-goal. Refresh plan review; TASK-190/197/198 retain transition authority.
<!-- SECTION:NOTES:END -->
