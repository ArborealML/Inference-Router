---
id: TASK-51
title: Bound queue admission when a caller supplies no deadline
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-18T20:23:00Z'
labels:
  - scheduling
  - wire
milestone: m-3
dependencies: []
priority: medium
ordinal: 51000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decision-25 requires every request to leave the queue within a finite time. Use measured switch and admission timings to specify a default bound when the caller supplies none. Cover both a declared engine that stays unhealthy and a healthy forwarding-only engine that never reports the requested model. Define the refusal and test the bound with a controlled clock.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 7 and 14.1 state the chosen posture for a queued request with no declared ceiling, with its reason
- [ ] #2 the worst case is a number in SPEC, not a promise: what a caller waits when nothing serves it
- [ ] #3 a test pins whichever posture is chosen, including the case with no option set
- [ ] #4 The chosen posture names the `auto_switch = false` case: a request for a declared model no loaded engine reports has no switch to wait for, and its bound is the same number
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Second trigger for the same permanent-wait path, measured 17:34-17:36Z on a live loopback router: engine **healthy**, model **declared** in the engine's config catalog, not reported loaded, `auto_switch = false`. The request was enqueued and answered nothing for the full 90 s of the probe — past `switch_wait_timeout_ms = 60000`, with no refusal of any kind — while a name nothing declares answered 503 `model_unavailable` immediately. So the admission fast-refuse rule (a model nothing here can serve) does not cover the case where a catalog declares a model that no switch will ever load, and the ceiling in this row is the only thing that would.
<!-- SECTION:NOTES:END -->
