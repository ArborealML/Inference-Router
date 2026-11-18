---
id: TASK-88
title: Let a client see the context window each model would actually be served in
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T18:52:00Z'
labels:
  - wire
  - serve
dependencies: []
priority: high
ordinal: 87000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when the top-tier review of context_window on ordinary GET /v1/models returns. The normal route must identify a model’s proven serving window for pi.
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
Extend ordinary GET /v1/models with a positive context_window in tokens for each model with a current proven serving window. Bind the window and listed model to the same engine snapshot. Verify the resident engine using the default URL. Exercise unknown, stale, duplicate ID and older responses and preserve the loaded-model list. Resolve the minimal coherent probe implementation in external wire review.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Wire-plan review submitted on 2026-11-18. The coordinator requires the default endpoint without an opt-in parameter. An older response may omit the field.
<!-- SECTION:NOTES:END -->
