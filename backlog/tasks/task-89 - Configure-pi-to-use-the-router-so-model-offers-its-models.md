---
id: TASK-89
title: Configure pi to use the router, so /model offers its models
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T19:54:00Z'
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
Wake when the deployed router serves the qualified catalog so the shared pi provider can declare the verified windows. The two-model path has been measured through a disposable router and pi session.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 pi names the router's loopback endpoint and the models it offers, in a config file a reader can find.
- [ ] #2 Each offered model declares its context window, so a /model choice is honest about what it buys.
- [x] #3 No change moves the pi lane an agent session is running on; that move waits for a named window and the owner's nod.
- [x] #4 Two verified models appear in /model and a real pi turn completes on each through the router.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Read back the current pi provider without printing secrets. Exercise /model and complete a real turn on the verified resident model in an isolated pi session. Record the model, window and client outcome. Repeat for the second model only after availability and authorization are established.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The initial client qualification on [5c5e18a](https://github.com/ArborealML/Inference-Router/commit/5c5e18a) completed real pi turns on two already-resident models. In a second run on the integrated discovery build, ordinary GET /v1/models returned 256000 tokens for resident Qwen and 8192 for resident Ternary Bonsai. Independent properties reads matched both values. The Bonsai model reports a training context of 262144, which differs from its 8192-token serving window.

The disposable pi 1.3.0 provider copied the two discovered values. In the same interactive session, /model selected each model and each completed a turn with stop. One local Qwen turn returned the requested marker, and the Dax turn answered 5. Filtering model IDs with a wildcard can hide the slash in the Bonsai ID. The shared provider file remained byte-identical and still declares 131072 for Qwen. Before checking #2, align the shared provider with the deployed catalog. TASK-112 owns native residency change.
<!-- SECTION:NOTES:END -->
