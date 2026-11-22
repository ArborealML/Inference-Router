---
id: TASK-160
title: Verify active cancellation reaches the native inference engine
status: Testing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:12:00Z'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The one-slot Maya backend reported disconnect and cancel=true 256 ms after the corresponding streamed AIR ticket was cancelled, before the next request. Strata live verification will match the native request and subsequent slot use.
<!-- SECTION:NOTES:END -->
