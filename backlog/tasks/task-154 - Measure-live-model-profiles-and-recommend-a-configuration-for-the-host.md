---
id: TASK-154
title: Measure live model profiles and recommend a configuration for the host
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T01:17:00Z'
labels: []
dependencies: []
priority: high
ordinal: 149000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The coordinator is scoping the live tuning executor. Given a model, host and workload requirements, identify the best measured eligible profile and concurrency. Reuse the shipped workload planner and offline comparison. The operator controls deployment.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An authorized real run produces a comparison-compatible record tied to the observed model, launch profile and hardware.
- [ ] #2 Record context, concurrency, aggregate output throughput, time to first token and failures for each setting. Incomplete measurements cannot win.
- [ ] #3 Report the highest median aggregate throughput meeting the workload requirements or report that none qualifies. Include the measured alternatives and supporting evidence.
- [ ] #4 Demonstrate the recommendation on one model and host with at least two measured settings. Applying it is a separate operator action.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
First run the short grid against one verified resident and emit a comparison-compatible summary. Reuse that executor across eligible configured profiles and rank passing measurements. Qualify long-context recommendations with the separate long workload.
<!-- SECTION:PLAN:END -->
