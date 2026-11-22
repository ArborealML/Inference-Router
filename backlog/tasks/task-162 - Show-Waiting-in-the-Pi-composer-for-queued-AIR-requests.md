---
id: TASK-162
title: Show Waiting in the Pi composer for queued AIR requests
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:47:00Z'
labels: []
dependencies: []
priority: high
ordinal: 157000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Display Waiting in the active Pi composer while the current AIR ticket is queued.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Queued tickets show Waiting and dispatch restores the generation indicator.
- [x] #2 Cancel, completion, error, branch changes and reload clear stale state without affecting another session.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Display Waiting in the active Pi composer while the current AIR ticket is queued.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A real Pi terminal displayed Waiting while the ticket queued and Working after dispatch. The installed extension also passed cancellation, reload, branch change and transient polling recovery checks. [Merged implementation](https://github.com/ArborealML/Inference-Router/pull/78).
<!-- SECTION:NOTES:END -->
