---
id: TASK-51
title: Decide the ceiling for a queued request that declared no max_queue_time
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-18T17:47:00Z'
labels:
  - scheduling
  - wire
dependencies: []
priority: medium
ordinal: 51000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The only permanent-wait path left after 83ebc75: a request for a model an engine declares, while that engine is unhealthy, is queued with no timer armed at all — the queue bound is armed only when the caller sets max_queue_time, and MaybeFailDeadline needs the option — so it waits until the caller disconnects. A posture choice with a latency consequence, not a bug fix: keep the caller opt-out (a patient caller waits indefinitely, nothing in the router bounds it) or give the queue a default ceiling (bounded worst case; a request that would eventually be served is refused at the ceiling instead). Write which into SPEC 7 next to the fast-refuse rule and into 14.1, with the reason and the worst-case number either way. If the option analysis wants an external pass, hand this task to the research service rather than settling it in a commit message.
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
