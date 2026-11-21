---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-21T03:12:00Z'
labels:
  - scheduling
  - wire
milestone: m-3
dependencies: []
priority: high
ordinal: 52000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The built-in scheduler refuses a request before queueing when its explicit context floor exceeds every applicable configured tier across all eligible declarers. An eligible declarer without an applicable ladder prevents this refusal. An estimated context alone does not trigger it. Landed in [PR #72](https://github.com/ArborealML/Inference-Router/pull/72).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [x] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The prior admission code queued the explicit over-ceiling request. Restoring the change passed both focused controls. The scheduler comparison fixture uses an estimated prompt so it continues to exercise queued selection. The refreshed candidate passed 86 focused Release tests. [Exact main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44859464948) passed on 1b15a81.
<!-- SECTION:NOTES:END -->
