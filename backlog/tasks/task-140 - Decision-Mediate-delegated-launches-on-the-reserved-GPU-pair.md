---
id: TASK-140
title: 'Decision: Mediate delegated launches on the reserved GPU pair'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-19T19:54:00Z'
updated_date: '2026-11-19T19:58:00Z'
labels: []
dependencies: []
priority: high
ordinal: 138000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner directed the initial version to assume system resources dedicated to inference. [Decision 71](../decisions/decision-71%20-%20Dedicate%20initial%20deployment%20resources%20to%20inference.md) records this and distinguishes it from the coordinator’s engineering choice for enforcing the router-managed lifecycle owner. Offline implementation can proceed. Host cutover remains separate.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Record the owner’s choice of broker-only delegated mutation for offline implementation or continued unrestricted delegation with enrollment deferred.
<!-- AC:END -->
