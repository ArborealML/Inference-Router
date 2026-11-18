---
id: TASK-126
title: Bound an individual readiness probe by the remaining switch deadline
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:43:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 124000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The readiness loop checks its deadline before calling the state and model endpoints. A pending HTTP call can outlive the remaining budget. Preserve external cancellation and fail the attempt through its existing terminal policy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hold a state probe and then a models probe separately past the remaining switch budget. Each attempt settles within the named bound and produces one terminal result.
- [ ] #2 A delayed successful reply after timeout cannot publish readiness or revive a retired attempt. A healthy alternate can still make progress.
<!-- AC:END -->
