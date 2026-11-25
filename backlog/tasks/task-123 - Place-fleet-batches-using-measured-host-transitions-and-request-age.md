---
id: TASK-123
title: Place fleet batches using measured host transitions and request age
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
updated_date: '2026-11-25T02:28:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-122
  - TASK-71
  - TASK-72
  - TASK-110
ordinal: 121000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the placement part of TASK-78. Choose an eligible host and model batch from the global queue. Each host retains authority over admission and lifecycle.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 On the same arrivals, changing only measured transition times changes a host or batch choice. A host refusal excludes that candidate even when it has the cheapest estimate.
- [ ] #2 With bounded work and continuing arrivals, an eligible aged request completes within the stated bound. A lost heartbeat never counts as a vacant host.
- [ ] #3 Placement checks each host, engine, model and profile context ceiling before requesting a switch. At the HyperQwen ceiling a request is eligible; one token over causes zero launches there and can use another eligible placement.
<!-- AC:END -->
