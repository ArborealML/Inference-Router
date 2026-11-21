---
id: TASK-150
title: Restore main CI for the admin switch acknowledgment deadline
status: Done
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-21T02:12:00Z'
labels: []
dependencies: []
priority: high
ordinal: 148000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames diagnosed [main CI run 44853950018](https://github.com/ArborealML/Inference-Router/actions/runs/44853950018) at 6c0aad6. The Ubuntu build + test job failed in Test at AdminSwitchOperationTests.AdminSwitch_AckCompletesJustBeforeDeadline, line 503. HasTimer was true after the client received the acknowledgment and the test observed gate exit. The other 1,923 cases passed. Native AOT was not reached. A controlled probe showed gate exit can precede timer retirement. [Recovery PR #74](https://github.com/ArborealML/Inference-Router/pull/74) waits for disposal before advancing the clock and verifies zero callbacks. Six focused cases passed and [main CI at b13c2d9](https://github.com/ArborealML/Inference-Router/actions/runs/44856985918) passed all three jobs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A controlled ordering check distinguishes acknowledgment completion, gate exit and deadline timer retirement while preserving just-before-deadline success and timeout behavior.
- [x] #2 The affected Release tests pass on the correction, and exact hosted main CI passes Release, Native AOT and smoke.
<!-- AC:END -->
