---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-18T18:40:00Z'
labels: []
dependencies:
  - TASK-111
  - TASK-110
  - TASK-108
  - TASK-114
  - TASK-25
  - TASK-64
ordinal: 110000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use the local router configuration and a real pi-clm client. TASK-85 and TASK-89 cover front-only serving and client setup. This milestone requires an observed native model transition and usable client responses. The owner parked host lifecycle operations at 17:55Z. Run the proof only on an authorized isolated host or in a subsequently granted window.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->
