---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
updated_date: '2026-11-20T00:33:00Z'
labels: []
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Configure HY4 Flash with seven explicit context tiers. The installed selector currently declares 32k only. Trace configuration, launcher and readiness inputs for each requested size and identify any missing launcher support. Keep 32k as the current default. Preserve the GLM/HY4 routing controls from the first review. Configured tiers are targets; host-specific serving capacity needs measured evidence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 HY4 and its model aliases resolve 32k, 64k, 96k, 128k, 256k, 512k and 1024k as distinct profiles at 1024 tokens per k. Omitting the tier retains the 32k default.
- [ ] #2 A focused offline check compares the configured HY4 target against the installed selector launch settings and safeguards; document any unsupported native setting or activation prerequisite.
- [ ] #3 The updated router configuration passes the config verb offline and identifies where operators enable the model.
- [ ] #4 Offline readback verifies the exact launch and probe window for each tier. Unsupported launches refuse explicitly; the router only admits requests against verified live capacity.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Amend the reviewed plan for the seven sizes. Reuse the parsed sample engine to exercise loaded and absent roster behavior. Verify that launch and probe resolve the exact selected window, including the largest tier; distinguish unsupported launch and unmeasured capacity from a successful qualification.
<!-- SECTION:PLAN:END -->
