---
id: TASK-138
title: Repair published evidence links for readiness closeout
status: Testing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T11:45:00Z'
labels: []
dependencies: []
priority: high
ordinal: 136000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The board contract job failed at dd44ca5 during the strict task check because the closed readiness task cited a qualification commit unavailable to a public checkout. [Failed job](https://github.com/ArborealML/Inference-Router/actions/runs/44516896248/job/135535252557).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The readiness closeout cites merged public evidence and the hosted board contract passes on the corrected tree.
<!-- AC:END -->
