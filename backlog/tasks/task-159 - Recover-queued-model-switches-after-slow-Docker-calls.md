---
id: TASK-159
title: Recover queued model switches after slow Docker calls
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:37:00Z'
labels: []
dependencies: []
priority: high
ordinal: 154000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Five-minute Docker calls must allow slow model shutdown while retaining the independent model-readiness budget. A failed launch must not silently strand later tickets.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 New defaults and installed config use 300000 ms; an explicit override still works.
- [ ] #2 After a slow stop, the next queued ticket progresses or receives a concrete safe failure.
- [ ] #3 A failed switch releases group ownership. Future requests recheck actual Docker state after repeated failures and recover without restarting AIR when the engine is safe to use.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Five-minute Docker calls must allow slow model shutdown while retaining the independent model-readiness budget. A failed launch must not silently strand later tickets.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The revised cancellation fixture passed both cases locally. All 53 affected admission and lifecycle checks passed. Verified an ordinary Pi ticket serves Qwen at 256k following the approved restart. Waiting for the final hosted suite on the integrated source.
<!-- SECTION:NOTES:END -->
