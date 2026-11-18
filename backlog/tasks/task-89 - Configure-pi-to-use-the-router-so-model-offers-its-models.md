---
id: TASK-89
title: Configure pi to use the router, so /model offers its models
status: Preparing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T18:46:00Z'
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
- [ ] #3 No change moves the pi lane an agent session is running on; that move waits for a named window and the owner's nod.
- [ ] #4 Two verified models appear in /model and a real pi turn completes on each through the router.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Read back the current pi provider without printing secrets. Exercise /model and complete a real turn on the verified resident model in an isolated pi session. Record the model, window and client outcome. Repeat for the second model only after availability and authorization are established.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The existing pi router provider already declares one model and its context window. Extend its verified model entries as residency is proven. Exercise /model in a test session and select the newly available model. Confirm the response through the router and keep active agent lanes on their existing settings.
<!-- SECTION:NOTES:END -->
