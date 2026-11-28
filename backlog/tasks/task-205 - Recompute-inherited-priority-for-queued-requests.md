---
id: TASK-205
title: "Recompute inherited priority for queued requests"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 205000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enqueue copies inherited priority into immutable pending nodes; updating the thread resorts stale values and registration uses a different update/wake path. Fix in place. TASK-185 owns Pi and wire parsing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Promotion and demotion reorder inherited pending requests; explicit overrides and FIFO enqueue sequence are unchanged.
- [ ] #2 Register/update share the same mutation, event and wake contract. Selection racing an update revalidates the exact pending candidate before admission.
- [ ] #3 Priority changes neither inflight cap nor active work, provider score, spend, context, admission target or physical lanes.
- [ ] #4 SPEC §§5.1 and 5.5 and focused race tests pin ordering and terminal behavior.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse existing synchronization and event/wake paths. Track provenance of inherited versus explicit priority; replace only affected pending values without re-enqueueing. Test repeated updates and a paused exact take.
<!-- SECTION:PLAN:END -->
