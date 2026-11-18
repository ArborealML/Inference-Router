---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - scheduling
  - wire
milestone: m-3
dependencies: []
priority: low
ordinal: 52000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Deliberately excluded from the fast-refuse work at 83ebc75, for a reason worth keeping: when the caller sends the required_context header the figure is exact and refusal is safe; when it does not, the figure comes from EstimateContextTokens over prompt characters and refusing on it could reject a request whose estimate was pessimistic. So the safe half is the explicit-header case: no profile on any engine that declares the model can fit the declared context, refused with the already-declared code rather than queued. The estimated case stays as it is until someone decides whether the estimate is good enough to refuse on. No new wire shape for either, and the lesson from 83ebc75 applies: check where the request is refused, not only where it is scheduled.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->
