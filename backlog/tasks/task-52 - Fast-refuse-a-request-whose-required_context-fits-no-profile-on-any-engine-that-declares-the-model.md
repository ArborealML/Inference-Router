---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-20T23:03:00Z'
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
Wake on independent review of the revised qualification plan. The admission proof uses explicit context demand and the configured tier ladder. A wider observed window cannot override an applicable nonfitting ladder in the built-in scheduler. An eligible engine without an applicable ladder precludes this fast refusal. Config handed off the amended plan for review.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan review accepted the admission proof and requested stronger controls. Use the same body with and without the explicit header, with an estimate genuinely above the ceiling. Reload to add a distinct external engine declaring a wider tier, then require admission to reconsider the old proof. Cover a missing ladder, alternate-case lookup, no applicable ladders anywhere, exact fit, custom scheduling and a wider observed window. Submit the amended plan for independent review before implementation.
<!-- SECTION:NOTES:END -->
