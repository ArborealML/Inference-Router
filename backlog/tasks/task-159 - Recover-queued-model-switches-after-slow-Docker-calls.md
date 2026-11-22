---
id: TASK-159
title: Recover queued model switches after slow Docker calls
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-22T03:06:00Z'
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
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Five-minute Docker calls must allow slow model shutdown while retaining the independent model-readiness budget. A failed launch must not silently strand later tickets.
<!-- SECTION:PLAN:END -->
