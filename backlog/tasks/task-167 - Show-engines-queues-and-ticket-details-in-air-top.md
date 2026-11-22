---
id: TASK-167
title: Show engines, queues and ticket details in air top
status: Testing
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-22T04:09:00Z'
updated_date: '2026-11-22T04:33:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-166
references:
  - src/Arboreal.InferenceRouter.Cli/Program.cs
ordinal: 162000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build the keyboard-driven monitor over the shared monitoring state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Engine rows distinguish reported residency and switch target, with serving, switching and unhealthy engines first.
- [ ] #2 Queue selection filters activity. Depth uses the router snapshot even when retained history contains fewer tickets.
- [ ] #3 Admissions, dispatches, completions, cancellations and failures show time and available reasons. Enter follows a stable ticket ID, Escape returns and q quits.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Render engine state and switch target separately, router-provided queue depths, and retained activity. Select by stable ticket ID across updates. Sanitize server strings and marshal redraws to the UI thread.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Public air top at 4b5034b compiled and three focused cases passed. The table retains its viewport, details can scroll, and frames have an explicit ownership contract. Actual terminal checks and independent review are underway.
<!-- SECTION:NOTES:END -->
