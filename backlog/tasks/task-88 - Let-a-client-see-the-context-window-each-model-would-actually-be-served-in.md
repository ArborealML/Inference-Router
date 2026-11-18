---
id: TASK-88
title: Let a client see the context window each model would actually be served in
status: In Progress
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T19:25:00Z'
labels:
  - wire
  - serve
milestone: m-0
dependencies: []
priority: high
ordinal: 87000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement model-specific context discovery on ordinary GET /v1/models for pi. Preserve each loaded row and its owning engine.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A caller can learn, for each model the router will serve, the context window it would be served in, from a named surface stated in SPEC.
- [ ] #2 The /v1 surface stays OpenAI-compatible: any added member is written into SPEC 6, and a pinned client tolerates its absence.
- [ ] #3 A stale or unknown window never authorises a profile the engine cannot serve (pairs with TASK-68 AC #1).
- [x] #4 The plan for any change to a /v1 body goes to a top-tier review before code exists.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend ordinary GET /v1/models with a positive context_window in tokens for each model with a current proven serving window. Bind the window and listed model to the same engine snapshot. Verify the resident engine using the default URL. Exercise unknown, stale, duplicate ID and older responses and preserve the loaded-model list. Resolve the minimal coherent probe implementation in external wire review.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Wire plan reviewed against 5c5e18a. Use one immutable observation for the model ID and its effective window. Identical IDs on different engines retain independent values. Suppress ambiguous evidence within one engine, withdraw stale proof, and keep discovery independent of admission. Qualify one actual backend and exercise legacy clients.

The resident model-list row reported 256000 tokens. Independent authenticated backend health reported the same model alias, effective generation context 256000, four slots and Strata 0.1.60. Qualify the matching build and verify the value through ordinary AIR model discovery before accepting the wire criterion.
<!-- SECTION:NOTES:END -->
