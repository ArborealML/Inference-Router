---
id: TASK-220
title: 'Decision: Authorize connected publication for this implementation session'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-27'
updated_date: '2026-11-27'
labels: []
milestone: m-10
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Claire must choose whether this implementation session may publish through the connected GitHub account with explicit agent provenance. Local Git cannot authenticate a push, and the available commit tool cannot set author/committer. TASK-70's planning-only exception does not authorize implementation publication. This decision concerns publication capability; TASK-70 separately owns preventive range enforcement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The owner explicitly authorizes a scoped publication exception or selects prescribed-identity publication through an authenticated Git capability.
- [ ] #2 The chosen scope and provenance requirements are recorded without changing CI acceptance criteria or granting merge/live-operation authority.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review candidate TASK-187 source 56b24d3cbb21e2554454cc8931f20d03dc175488: 150 focused tests passed; separate source review passed after correction. Prepared PR packet identifies scope, exact local evidence, remaining hosted checks and downstream contracts. Answer in this implementation session: authorize connector publication for these implementation PRs with explicit Sol/Astra provenance, or preserve strict Git identities and provide an authenticated push capability. Decision takes approximately one minute. No implementation merge, paid request, deployment or live host action is included.
<!-- SECTION:NOTES:END -->
