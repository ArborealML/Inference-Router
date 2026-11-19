---
id: TASK-133
title: Close refusal-category audit bypasses
status: To Do
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-19T06:27:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The readiness repair in PR #4 classifies all current exits. The source audit can accept a record clone that overwrites Detail, or a valid invocation beside an invalid one. Its assignment checks also miss compound writes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A negative control rejects changing the caller-facing failure detail through a record clone.
- [ ] #2 A valid invocation or comment on the same line cannot mask an unclassified failure.
- [ ] #3 Compound writes are rejected or the recorded reason is protected by construction. Document the existing HealthTimeout fallback for additional miss kinds.
<!-- AC:END -->
