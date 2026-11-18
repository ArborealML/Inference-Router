---
id: TASK-89
title: Configure pi to use the router, so /model offers its models
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T07:15:00Z'
labels:
  - serve
  - pi
dependencies: []
priority: high
ordinal: 88000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The second half of the owner's morning goal: pi must be able to pick router-served models with /model. This is configuration on the pi side, not router code, and it must not move the lane this session runs on.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 pi names the router's loopback endpoint and the models it offers, in a config file a reader can find.
- [ ] #2 Each offered model declares its context window, so a /model choice is honest about what it buys.
- [ ] #3 One pi turn completes through the router, and /model lists more than one model.
- [ ] #4 No change moves the pi lane an agent session is running on; that move waits for a named window and the owner's nod.
<!-- AC:END -->
