---
id: TASK-72
title: >-
  Drain the waiting queue by age-weighted value before switching, without
  ping-pong
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T01:24:00Z'
labels: []
dependencies:
  - TASK-69
priority: high
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner's example, taken literally: one request waiting on the loaded model and eight waiting on another model means the router serves all eight, then switches — with age weighting so an old request counts for more than a new one — and does not flip back after the first new arrival. This is the no-ping-pong rule from decision-25, and it is a queue and scheduler behaviour, not a lifecycle one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the owner's example is pinned: one waiter on the loaded model and eight on another are served eight-then-switch
- [ ] #2 under steady competing load the router does not alternate switches between two models, pinned by a test that counts switches
- [ ] #3 an old request's wait is bounded, the bound is named in SPEC, and the refusal that enforces it is observable on the feed
<!-- AC:END -->
