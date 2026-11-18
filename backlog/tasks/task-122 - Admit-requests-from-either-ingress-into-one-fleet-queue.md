---
id: TASK-122
title: Admit requests from either ingress into one fleet queue
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-51
  - TASK-77
priority: high
ordinal: 120000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement shared admission and delivery claims for the fleet milestone, TASK-78. Both ingress routers observe the same capacity and ticket state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Send concurrent requests through two ingress routers. Each accepted request has one queue entry, at most one active delivery claim and one terminal result.
- [ ] #2 Exercise disconnection during claim handoff. After uncertain upstream acceptance, a successor records one indeterminate terminal result and sends zero additional inference requests.
- [ ] #3 Test fleet capacity, cancellation and the no-option deadline through both ingress endpoints, including an unreachable host.
<!-- AC:END -->
