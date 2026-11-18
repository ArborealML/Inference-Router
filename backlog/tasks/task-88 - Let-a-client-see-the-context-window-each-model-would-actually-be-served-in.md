---
id: TASK-88
title: Let a client see the context window each model would actually be served in
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-18T20:23:00Z'
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
Wake on the pinned client compatibility result and TASK-68 admission proof. Model-specific discovery is deployed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A caller can learn, for each model the router will serve, the context window it would be served in, from a named surface stated in SPEC.
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
At [31179e3](https://github.com/ArborealML/Inference-Router/commit/31179e3) discovery publishes a positive window from the same upstream observation as its loaded model. Unknown and ambiguous evidence omits the field. Filtered duplicates and alias collisions withhold proof. The public capability API preserves its original array signatures. The coordinator ran 314 integrated Release cases, followed by 38 focused cases after strengthening the held-poll checkpoint. Runtime, fixture and reader documentation reviews were dispatched.

Ordinary GET /v1/models on the deployed router returned Qwen with owned_by strata and context_window 256000. The backend independently returned the same value under default_generation_settings.n_ctx and four total slots. The shared pi provider declares 256000 and a real turn succeeded. TASK-68 owns admission with stale gauges.

Review of 2edf7e7 raised case-only roster changes. Integrated WithModelsObservation compares exact spelling whenever either observation carries a window. Case-insensitive comparison remains the previous behavior when neither has window evidence. The public array signature matches the preceding main release; the intermediate list signature existed only on a worker branch.
<!-- SECTION:NOTES:END -->
