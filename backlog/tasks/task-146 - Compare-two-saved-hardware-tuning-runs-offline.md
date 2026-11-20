---
id: TASK-146
title: Compare two saved hardware tuning runs offline
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T19:07:00Z'
labels: []
milestone: m-14
dependencies: []
ordinal: 144000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake: independent rereview of the amended two-file comparison plan. Config delivered the schema and Linux file-admission amendments against the landed [hardware tuning design](../../docs/hardware-tuning-design.md). Delivery owns the verdict. This is the first executable increment of the tuning workflow.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Compare compatible saved runs at the same concurrency with reproducible deltas. Identify unknown or incomparable evidence explicitly.
- [ ] #2 Bound input and output and verify file identity on supported systems. Malformed input produces a redacted error with no partial report.
- [ ] #3 Exercise the comparison through the CLI with golden and negative controls and verify the published Native AOT binary.
<!-- AC:END -->
