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
Five concrete candidates are prepared: TASK-203, 187, 205, 202 and 204. The combined prepared aggregate 89783fbaf83bb31de3580030917a8ccf67f779ff passed 680 affected tests; distinct implementer/reviewer source and history audits passed. [Reviewable scope and source mappings](../milestones/wave1-implementation-evidence.md) identify exact evidence and remaining hosted gates. No response was selected in the session's publication choice; no exception or PR publication is claimed.

Answer in this implementation session: authorize connector publication for these implementation PRs with explicit Sol/Astra provenance, or preserve strict Git identities and provide an authenticated push capability. Decision takes approximately one minute. No implementation merge, paid request, deployment or live host action is included. TASK-70 retains independent ownership of preventive attribution enforcement.
<!-- SECTION:NOTES:END -->
