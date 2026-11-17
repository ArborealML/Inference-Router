---
id: TASK-20
title: 'Decision: how the router starts and stops engines'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
labels:
  - engine
  - lifecycle
dependencies: []
priority: high
ordinal: 20000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SPEC 5.3.1 still describes shelling out to a selector program. The reviewed alternatives are a native provider that talks to the Docker Engine API over its socket, or a thin seam that shells out to the docker CLI only. The native path removes quoting and process-tree cleanup bugs and gives real container identity, but it puts the router in charge of authorisation on a shared box, which is your call, not mine. Unblocks TASK-1.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The mechanism is chosen and the SPEC section that describes it is agreed
- [ ] #2 The authorisation posture for a container the router did not start is stated (refuse, adopt, or refuse-and-report)
<!-- AC:END -->
