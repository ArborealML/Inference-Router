---
id: TASK-168
title: Recover the terminal monitor after disconnects and event gaps
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-22T04:10:00Z'
updated_date: '2026-11-22T04:33:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-167
references:
  - SPEC.md
ordinal: 163000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Keep partial history and stale observations explicit during outages.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A disconnected monitor displays the last snapshot and its age, then resumes with the last sequence and refreshes status.
- [ ] #2 A retention gap is marked in the timeline. Reconnect and replay do not duplicate observed events.
- [ ] #3 Exercise concurrent clients, rapid queue changes and a failed switch while a ticket is selected.
- [ ] #4 Reconnect always marks continuity uncertain because the feed lacks a process epoch. Refresh status and the followed ticket on reconnect or a detected gap.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reconnect with cancellation and bounded backoff. Refresh gauges independently of event traffic. On uncertain continuity or sequence reset, refresh status and label incomplete history. Test overload, gaps, restart and quit.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reconcile snapshots and the exact followed ticket on reconnect. The client review identified mutable gauge storage and ambiguity at a lower first sequence after reconnect. Verify snapshot isolation and the new observation segment.
<!-- SECTION:NOTES:END -->
