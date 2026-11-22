---
id: TASK-168
title: Recover the terminal monitor after disconnects and event gaps
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:10:00Z'
updated_date: '2026-11-22T04:20:00Z'
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
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reconnect with cancellation and bounded backoff. Refresh gauges independently of event traffic. On uncertain continuity or sequence reset, refresh status and label incomplete history. Test overload, gaps, restart and quit.
<!-- SECTION:PLAN:END -->
