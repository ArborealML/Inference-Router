---
id: TASK-168
title: Recover the terminal monitor after disconnects and event gaps
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:10:00Z'
updated_date: '2026-11-22T04:48:00Z'
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
- [x] #1 A disconnected monitor displays the last snapshot and its age, then resumes with the last sequence and refreshes status.
- [x] #2 A retention gap is marked in the timeline. Reconnect and replay do not duplicate observed events.
- [x] #3 Exercise concurrent clients, rapid queue changes and a failed switch while a ticket is selected.
- [x] #4 Reconnect always marks continuity uncertain because the feed lacks a process epoch. Refresh status and the followed ticket on reconnect or a detected gap.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reconnect with cancellation and bounded backoff. Refresh gauges independently of event traffic. On uncertain continuity or sequence reset, refresh status and label incomplete history. Test overload, gaps, restart and quit.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The eight focused tests passed at 3339404. Reconnect marks uncertain continuity, a lower first sequence opens a new segment, and fresh status and ticket reads reconcile current state. Independent review found no outstanding material issue in the correction. [Implementation and verification](https://github.com/ArborealML/Inference-Router/pull/79).
<!-- SECTION:NOTES:END -->
