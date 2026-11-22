---
id: TASK-160
title: Verify active cancellation reaches the native inference engine
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:30:00Z'
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
- [x] #1 Correlate the original AIR ticket with backend cancellation and release of its active slot.
- [x] #2 Verify that another client can still finish on its own ticket.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace client cancellation through AIR and the running backend. Separate a queued cancellation, which has no native work, from cancellation while tokens are being generated.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
On installed Pi, /air-cancel after the first Qwen text delta produced a matching Strata disconnect 33 ms later and native completion accounting. One-slot Maya logged cancel=true 256 ms after AIR cancelled its active ticket. Another client finished on its original ticket in the previously qualified multi-client run. [Installed Pi cancellation path](https://github.com/ArborealML/Inference-Router/pull/77).
<!-- SECTION:NOTES:END -->
