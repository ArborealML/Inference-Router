---
id: TASK-131
title: Fix CI serving gauge status regression on the publication change
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T02:54:00Z'
updated_date: '2026-11-19T03:08:00Z'
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
- [x] #1 The focused status test passes with discriminating known and unknown capacity cases.
- [x] #2 The build and test job passes on the corrected pull request head.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [fixture change](https://github.com/ArborealML/Inference-Router/commit/8ae11bdcee563f13694b35e5863c827fbe5460b8) passed both cases locally and [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44448843444) passed 568 Release tests with zero skips. Review questioned whether slots could appear before model evidence. EngineRuntime publishes the serving tuple alongside the observed roster, and RouterCore reads both gauges from the same captured capability. Waiting for three slots distinguishes publication from startup zero. The paired cases exercise one matching model and a mixed reported roster.
<!-- SECTION:NOTES:END -->
