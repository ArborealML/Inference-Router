---
id: TASK-150
title: Restore main CI for the admin switch acknowledgment deadline
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-21T02:12:00Z'
labels: []
dependencies: []
priority: high
ordinal: 148000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames owns diagnosis of [main CI run 44853950018](https://github.com/ArborealML/Inference-Router/actions/runs/44853950018) at 6c0aad6. The Ubuntu build + test job failed in Test at AdminSwitchOperationTests.AdminSwitch_AckCompletesJustBeforeDeadline, line 503. HasTimer was true after the client received the acknowledgment and the test observed gate exit. The other 1,923 cases passed. Native AOT was not reached. Determine whether the observation precedes timer disposal or the timer outlives its owner.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A controlled ordering check distinguishes acknowledgment completion, gate exit and deadline timer retirement while preserving just-before-deadline success and timeout behavior.
- [ ] #2 The affected Release tests pass on the correction, and exact hosted main CI passes Release, Native AOT and smoke.
<!-- AC:END -->
