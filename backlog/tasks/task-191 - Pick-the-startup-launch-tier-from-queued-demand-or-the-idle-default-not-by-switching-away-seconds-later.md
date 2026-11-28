---
id: TASK-191
title: >-
  Pick the startup launch tier from queued demand or the idle default, not by
  switching away seconds later
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T23:57:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-22
dependencies:
  - TASK-183
  - TASK-197
  - TASK-198
priority: high
ordinal: 183000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fix the immediate startup relaunch regression before autonomous optimization. Start a configured fitting verified recipe for queued demand or the configured idle default; do not restart a sufficient resident merely because a wider-lane alternative exists.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Without demand, start configured idle default; no speculative resize.
- [ ] #2 With queued work, choose a configured verified fitting recipe and respect actual admission bounds; no unqualified or larger-lane preference.
- [ ] #3 A sufficient resident does not relaunch solely because another recipe has more lanes; necessary changes obey exact conformance and lifecycle rules.
- [ ] #4 Capture fixture reproduces/removes immediate gratuitous restart while preserving unrelated-provider progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use existing configured defaults, exact recipe and conformance; no measured estimator prerequisite. TASK-192 later reuses the same selection seam for qualified demand-driven optimization. Absent comparable evidence keep feasible resident/default.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Retains prior reported startup regression; no new live measurement.
<!-- SECTION:NOTES:END -->
