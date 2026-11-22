---
id: TASK-160
title: Verify active cancellation reaches the native inference engine
status: Testing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-22T03:06:00Z'
labels: []
dependencies: []
priority: high
ordinal: 155000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Trace client cancellation through AIR and the running backend. Separate a queued cancellation, which has no native work, from cancellation while tokens are being generated.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Correlate the original AIR ticket with backend cancellation and release of its active slot.
- [ ] #2 Verify that another client can still finish on its own ticket.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace client cancellation through AIR and the running backend. Separate a queued cancellation, which has no native work, from cancellation while tokens are being generated.
<!-- SECTION:PLAN:END -->
