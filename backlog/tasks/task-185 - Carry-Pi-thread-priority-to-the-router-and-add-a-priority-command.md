---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:54:00Z'
labels: []
dependencies: []
priority: high
ordinal: 177000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router already admits a caller priority on the inference path and uses it in scheduling, but the Pi-side extension never sends one, so every Pi session arrives at the configured default. Add the header on every request and a /priority slash command that sets it per session, with the current value shown in the status surface. The scheduler's use of priority for drain order is TASK-72 and is not part of this task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every inference request from the extension carries a priority the router parses.
- [ ] #2 /priority sets the value for the session and reports it back.
- [ ] #3 The value survives a session restart and shows in the extension's status view.
- [ ] #4 A rejected priority value produces the router's stated refusal, not a silent default.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Extension-side only; the router surface already exists (SPEC §6 header list). Pairs with TASK-183: priority and required context are the two demand axes a caller owns.
<!-- SECTION:NOTES:END -->
