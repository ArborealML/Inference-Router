---
id: TASK-88
title: Let a client see the context window each model would actually be served in
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T07:15:00Z'
updated_date: '2026-11-19T15:11:00Z'
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
Deployed model discovery and pinned client compatibility passed. [PR #8](https://github.com/ArborealML/Inference-Router/pull/8) completed the remaining stale-window admission criterion: exact ticket selection rechecks measured capacity, and insufficient or unknown capacity prevents dispatch.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A caller can learn, for each model the router will serve, the context window it would be served in, from a named surface stated in SPEC.
- [x] #2 The /v1 surface stays OpenAI-compatible: any added member is written into SPEC 6, and a pinned client tolerates its absence.
- [x] #3 A stale or unknown window never authorises a profile the engine cannot serve (pairs with TASK-68 AC #1).
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

Review of the discovery implementation integrated in 31179e3 raised case-only roster changes. Integrated WithModelsObservation compares exact spelling whenever either observation carries a window. Case-insensitive comparison remains the previous behavior when neither has window evidence. The public array signature matches the preceding main release; the intermediate list signature existed only on a worker branch.

The OpenAI JavaScript SDK 7.19.0 installed with pi 1.3.0 read the live deployed response with context_window 256000. Against a loopback legacy response omitting just that member, the same SDK returned identical standard row fields. The [reader guide](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md) documents filtering, alias handling and roster lifetime.

A forwarding-only deployment advertised 256000 for one loaded model. The backend independently reported 256000 and four slots, pi declared 256000, and the static engine capability declared 131072. No serving profile was configured. This proves divergent metadata. For AC #3 keep those discovery and static values fixed, withdraw current serving proof and observe the admission result for a profile requiring 256000.

The previously proposed live follow-up is covered by the landed deterministic admission controls. AWindowShrinkBeforeExactTakeLeavesTheExplicitTicketQueued holds selection across a window shrink with the same roster, observes zero sends and then permits the same ticket after capacity returns. The unknown-window and joined-floor controls distinguish explicit requirements from model-only dispatch.
<!-- SECTION:NOTES:END -->
