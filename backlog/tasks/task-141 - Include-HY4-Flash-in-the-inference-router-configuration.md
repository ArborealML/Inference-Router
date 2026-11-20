---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: Waiting
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
updated_date: '2026-11-20T00:41:00Z'
labels: []
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for independent review of the seven-tier HY4 plan on 3f349466. Its verdict wakes Config. This supersedes the single-tier plan. The installed selector defaults to 32k. Its outer CLI rejects 64k, 96k and 1024k; its HY4 map also lacks 128k, 256k and 512k. The router plan covers seven configured targets and refusal when measured capacity is insufficient. End-to-end wider launches still need selector, preflight and readiness support. Hardware fit remains unmeasured.
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
