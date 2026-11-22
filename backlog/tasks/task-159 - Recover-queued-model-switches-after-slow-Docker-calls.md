---
id: TASK-159
title: Recover queued model switches after slow Docker calls
status: Testing
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:30:00Z'
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
The five-minute setting is installed. A new Pi Qwen request streamed at 262144 tokens and one slot. The lifecycle writer is correcting the cancel-after-first-sibling-stop regression seen on ebb071d: CI expected one earlier stop and observed zero. Related local rows exposed incorrect cancellation error mapping. Verify both cases and the full hosted Release job. [Failing CI](https://github.com/ArborealML/Inference-Router/actions/runs/44980530915/job/137079837258).
<!-- SECTION:NOTES:END -->
