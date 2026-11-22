---
id: TASK-158
title: Resolve admin switch acknowledgement deadline CI timeout
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-22T02:39:00Z'
updated_date: '2026-11-22T02:41:00Z'
labels: []
dependencies: []
priority: high
ordinal: 153000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Ubuntu build and test job on fe7be9b timed out after 45 seconds in the write case of AdminSwitch_AckDeadlineWriteOrCompletionOrchestration. The rest of the suite passed. [Failing run](https://github.com/ArborealML/Inference-Router/actions/runs/44976514347/job/137067582227).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Verify the original acknowledgement deadline assertions locally and in the hosted Release run.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Inspect the timed-out wait and its scheduling handshake, fix the demonstrated failure, and verify both theory rows and the hosted job.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Waiting for hosted CI. The test now holds its close observer through the original peer assertion. The corrected theory and related transport cases passed 11/11 locally. [Correction](https://github.com/ArborealML/Inference-Router/commit/70625b4).
<!-- SECTION:NOTES:END -->
