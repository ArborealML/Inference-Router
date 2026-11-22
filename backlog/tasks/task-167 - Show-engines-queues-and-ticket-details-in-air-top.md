---
id: TASK-167
title: Show engines, queues and ticket details in air top
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:09:00Z'
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
