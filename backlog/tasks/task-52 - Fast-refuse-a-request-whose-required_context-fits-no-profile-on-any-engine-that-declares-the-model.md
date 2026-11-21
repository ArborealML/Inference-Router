---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-21T00:24:00Z'
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
.120 landing wakes integration of the qualified context-admission candidate 7321cfa. The built-in scheduler uses the configured tier ladder for the explicit-header refusal. An eligible engine without an applicable ladder precludes this refusal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The 12 focused Release tests passed. With the prior admission source the intended rejection was queued; restoration passed both focused controls. Source review confirmed PrefixAwareScheduler is sealed, and the unrelated custom scheduler control passed. Delivery owns exact integrated qualification and hosted checks.
<!-- SECTION:NOTES:END -->
