---
id: TASK-88
title: Let a client see the context window each model would actually be served in
status: Preparing
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T18:46:00Z'
labels:
  - wire
  - serve
dependencies: []
priority: high
ordinal: 87000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner goal: pi swaps models with /model. A picker needs, per model, the window it would get — and the router knows this (the serving gauges) but says nothing of it on /v1/models. This row decides where that belongs without breaking OpenAI compatibility. Blocks the honest half of TASK-89.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A caller can learn, for each model the router will serve, the context window it would be served in, from a named surface stated in SPEC.
- [ ] #2 The /v1 surface stays OpenAI-compatible: any added member is written into SPEC 6, and a pinned client tolerates its absence.
- [ ] #3 A stale or unknown window never authorises a profile the engine cannot serve (pairs with TASK-68 AC #1).
- [ ] #4 The plan for any change to a /v1 body goes to a top-tier review before code exists.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Verify the window and loaded model belong to one capability generation. Specify the per-model field on /v1/models, unknown and stale handling, and a compatibility pin for older responses. Review the wire plan before implementation.
<!-- SECTION:PLAN:END -->
