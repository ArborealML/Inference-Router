---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T02:50:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-125
  - TASK-110
  - TASK-108
  - TASK-114
  - TASK-25
  - TASK-64
  - TASK-118
ordinal: 110000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Prove the first end-to-end native model swap on the primary local host through a real pi-clm client. Both turns and both observed model residencies must be on that same host. TASK-85 and TASK-89 established forwarding and client selection. After this proof, expand to the peer host and its verified model inventory. Live mutation on the primary host still requires an authorized window.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->
