---
id: TASK-204
title: "Separate stateless admission from named thread limits"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 204000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Default HTTP and CLI calls synthesize the shared anon identity, whose default thread cap can serialize unrelated work across providers. Preserve explicit named-thread concurrency and affinity; omitted identity becomes request-scoped and retires with the ticket.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Concurrent omitted-thread HTTP and CLI calls dispatch independently, including different models; no permanent transient thread or cache entry remains.
- [ ] #2 Two explicitly shared named-thread requests respect their cap; status identifies the blocking scope.
- [ ] #3 SPEC §§5.1, 6 and 11 specify omitted identity, reserved legacy sentinel migration, and compatibility for deliberate named threads.
- [ ] #4 Malformed or conflicting identity is refused before queue effects; a deliberately named identity keeps its configured limits.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace both default ingress and CLI normalization; introduce a transient identity with existing queue lifecycle and bound its lifetime. Make legacy anon behavior an explicit migration, not a larger shared cap. Test cancellation, retirement and named-thread parity with capture backends.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
No change to priority, thread weight or provider scoring. TASK-207 owns subsequent credential-bound session identity and root authorization. Transient scheduling identity confers no trust or policy privilege.
<!-- SECTION:NOTES:END -->
