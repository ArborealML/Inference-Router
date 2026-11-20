---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
updated_date: '2026-11-20T00:49:00Z'
labels: []
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config is implementing the seven-tier offline sample, catalog and projection on 7d664a5 after independent plan approval. Load the edited shipped sample, observe its discovery and probe requests and matching capability snapshot, then exercise fitting and refused tickets. Use 96k as the normal AIR default; keep 32k explicitly selectable for safe operation. The installed selector still defaults to 32k. The six wider tiers remain catalog intentions pending selector CLI, HY4 launch and readiness support, and hardware qualification.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 HY4 and its model aliases resolve 32k, 64k, 96k, 128k, 256k, 512k and 1024k as distinct profiles at 1024 tokens per k. Omitting the tier selects 96k; 32k requires explicit selection.
- [ ] #2 A focused offline check compares the configured HY4 target against the installed selector launch settings and safeguards; document any unsupported native setting or activation prerequisite.
- [ ] #3 The updated router configuration passes the config verb offline and identifies where operators enable the model.
- [ ] #4 Offline readback verifies the exact launch and probe window for each tier. Unsupported launches refuse explicitly; the router only admits requests against verified live capacity.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Amend the reviewed plan for the seven sizes. Reuse the parsed sample engine to exercise loaded and absent roster behavior. Verify that launch and probe resolve the exact selected window, including the largest tier; distinguish unsupported launch and unmeasured capacity from a successful qualification.
<!-- SECTION:PLAN:END -->
