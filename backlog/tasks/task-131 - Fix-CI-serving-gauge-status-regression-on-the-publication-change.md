---
id: TASK-131
title: Fix CI serving gauge status regression on the publication change
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-19T02:54:00Z'
labels: []
dependencies: []
priority: high
ordinal: 129000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On 7751fa3 the build and test job in [CI run 44446986163](https://github.com/ArborealML/Inference-Router/actions/runs/44446986163) failed ServerWireTests.Status_Exports_Serving_Gauges. It expected 131072 and observed 0. Verify the fixture roster and wire contract, distinguishing model-bound capacity from ambiguous or unknown state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The focused status test passes with discriminating known and unknown capacity cases.
- [ ] #2 The build and test job passes on the corrected pull request head.
<!-- AC:END -->
