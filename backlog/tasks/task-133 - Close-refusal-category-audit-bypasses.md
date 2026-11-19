---
id: TASK-133
title: Close refusal-category audit bypasses
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T06:36:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for the independent plan review to return. The readiness repair in PR #4 classifies current exits. Its audit can accept a record clone that overwrites Detail, a valid invocation beside an invalid one, and compound writes to the recorded reason.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A negative control rejects changing the caller-facing failure detail through a record clone.
- [ ] #2 A valid invocation or comment on the same line cannot mask an unclassified failure.
- [ ] #3 Compound writes are rejected or the recorded reason is protected by construction. Document the existing HealthTimeout fallback for additional miss kinds.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review the test-only syntax audit before implementation. Check individual factory invocations, clone initializers and recorded-reason assignments. Use in-memory negative controls and preserve the health_timeout and switch_error defaults. Qualify with RefusalCategoryTests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The independent plan verdict wakes the coordinator. The landed audit matches the reviewed PR #4 head. The reviewer will assess the proposed parser dependency against cheaper alternatives.
<!-- SECTION:NOTES:END -->
