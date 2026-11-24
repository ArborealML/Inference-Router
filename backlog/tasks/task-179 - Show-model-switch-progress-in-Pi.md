---
id: TASK-179
title: Show model switch progress in Pi
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-24T01:36:00Z'
updated_date: '2026-11-24T01:41:00Z'
labels: []
dependencies: []
ordinal: 173000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Show completed switch milestones on the owning request while AIR prepares a local model.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Display observed progress and current phase during a switch, distinguish queueing from switching and clear the indicator at execution or failure.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Publish the count of completed local switch milestones on the owning ticket, then render that count as a labeled percentage in the composer and footer. Verify with a held readiness probe and isolated Pi terminal.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Four cold-owner tests verify readiness progress and queued-peer isolation. Three isolated Pi terminal tests passed, including switching, dispatch and prompt progress.
<!-- SECTION:NOTES:END -->
