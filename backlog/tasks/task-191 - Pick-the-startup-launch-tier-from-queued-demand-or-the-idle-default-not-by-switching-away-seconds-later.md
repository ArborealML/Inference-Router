---
id: TASK-191
title: >-
  Pick the startup launch tier from queued demand or the idle default, not by
  switching away seconds later
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T23:57:00Z'
labels: []
milestone: m-16
dependencies:
  - TASK-183
  - TASK-192
priority: high
ordinal: 183000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Choose startup recipe from actual eligible queued demand or configured idle default. Avoid an immediate relaunch simply because a fixed wider-lane tier was preferred. Preserve declared idle default.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Without queued demand, start configured idle default; no speculative demand or gratuitous resize.
- [ ] #2 With eligible work, select fitting verified recipe and admission target using actual finite demand and comparable evidence.
- [ ] #3 A sufficient resident does not switch merely because more lanes exist; necessary or demonstrably beneficial changes follow safe transition rules.
- [ ] #4 A fixture reproduces and removes unnecessary immediate relaunch, preserving independent provider progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TASK-192 evaluator rather than a second controller. Freeze chosen recipe and verify conformance; absent comparable evidence retain configured safe default.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Retains prior reported startup regression; no new live measurement.
<!-- SECTION:NOTES:END -->
