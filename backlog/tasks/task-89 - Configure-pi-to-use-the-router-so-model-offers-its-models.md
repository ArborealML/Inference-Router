---
id: TASK-89
title: Configure pi to use the router, so /model offers its models
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T20:21:00Z'
labels:
  - serve
  - pi
milestone: m-0
dependencies: []
priority: high
ordinal: 88000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on coordinated availability of a second inference model for the deployed router. The shared provider now agrees with its deployed Qwen catalog.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 pi names the router's loopback endpoint and the models it offers, in a config file a reader can find.
- [x] #2 Each offered model declares its context window, so a /model choice is honest about what it buys.
- [x] #3 No change moves the pi lane an agent session is running on; that move waits for a named window and the owner's nod.
- [x] #4 Two verified models appear in /model and a real pi turn completes on each through the router.
- [ ] #5 The deployed endpoint offers two verified, available models during a client qualification and an interactive pi session completes a turn on each using that endpoint.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Read back the current pi provider without printing secrets. Exercise /model and complete a real turn on the verified resident model in an isolated pi session. Record the model, window and client outcome. Repeat for the second model only after availability and authorization are established.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At [31179e3](https://github.com/ArborealML/Inference-Router/commit/31179e3), deployed GET /v1/models returned Qwen with a 256000-token window. The coordinator updated only that window in the shared pi provider and verified a real ephemeral pi turn returned AIR_CONTEXT_OK. No active agent session was moved.

Earlier, an isolated router and pi 1.3.0 reported the observed 256000-token Qwen window and 8192-token Ternary Bonsai window. In one interactive session, /model selected each and both served turns. Dax reported a 262144-token training context and 8192-token serving context. This qualifies the client behavior. The deployed two-model result and the native residency transition under TASK-112 remain distinct.
<!-- SECTION:NOTES:END -->
