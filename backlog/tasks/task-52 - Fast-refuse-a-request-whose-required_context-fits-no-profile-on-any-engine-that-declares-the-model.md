---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-20T23:19:00Z'
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
Wake on explicit implementation release when Config is available. The independently reviewed admission plan covers explicit context demand and the configured tier ladder. A wider observed window cannot override an applicable nonfitting ladder in the built-in scheduler. An eligible engine without an applicable ladder precludes this fast refusal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent rereview passed the amended plan on 7ee853e. The same body distinguishes explicit demand above the ceiling, an estimate above the ceiling and exact fit. Reloading a wider external declarer forces reconsideration before allocating a ticket. Separate cases cover missing ladders, ordinal matching, custom scheduling and the observed 131072-token window. Relevant source remains unchanged through 1b2c17f. No tests have run for this plan. Revision allocation awaits release.
<!-- SECTION:NOTES:END -->
