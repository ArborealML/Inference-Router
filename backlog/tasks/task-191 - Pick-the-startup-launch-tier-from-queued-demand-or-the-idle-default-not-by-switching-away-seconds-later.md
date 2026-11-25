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
dependencies: []
priority: high
ordinal: 183000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
At startup on the live host the router launched the default model at the narrow-slot tier and switched to the wide-slot tier of the same window about ninety seconds later, costing one full engine reload and killing the sessions that had just connected. That is a separate decision from serving an existing demand on a resident engine: it is about what to launch when nothing is queued yet. The fix must not touch the declared default tier, which the owner has ruled stays as configured.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 With no queued demand, startup launches the declared idle default and does not guess a wider tier.
- [ ] #2 With demand queued, startup commits one launch chosen against the measured host budget.
- [ ] #3 No switch follows the initial launch when a wider tier of the same window would already have satisfied the queued demand.
- [ ] #4 A pin shows a launch that is immediately followed by a tier switch, and then shows it gone.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Split out of the containment plan by the external plan review, which called it a hidden second plan.
<!-- SECTION:NOTES:END -->
