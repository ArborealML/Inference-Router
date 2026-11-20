---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: Done
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
updated_date: '2026-11-20T02:08:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The shipped shared Maya catalog includes seven HY4 Flash profiles. AIR defaults to 96k and accepts an explicit 32k safe option. Forwarding requires a loaded canonical roster entry and measured capacity. The installed selector supports 32k. Wider native launch and hardware fit require separate qualification.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 HY4 and its model aliases resolve 32k, 64k, 96k, 128k, 256k, 512k and 1024k as distinct profiles at 1024 tokens per k. Omitting the tier selects 96k; 32k requires explicit selection.
- [x] #2 A focused offline check compares the configured HY4 target against the installed selector launch settings and safeguards; document any unsupported native setting or activation prerequisite.
- [x] #3 The updated router configuration passes the config verb offline and identifies where operators enable the model.
- [x] #4 Offline readback verifies the exact launch and probe window for each tier. Unsupported launches refuse explicitly; the router only admits requests against verified live capacity.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Amend the reviewed plan for the seven sizes. Reuse the parsed sample engine to exercise loaded and absent roster behavior. Verify that launch and probe resolve the exact selected window, including the largest tier; distinguish unsupported launch and unmeasured capacity from a successful qualification.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed in [PR #39](https://github.com/ArborealML/Inference-Router/pull/39). Nine focused Release cases passed on 6263b83, both dedicated configuration loads passed, and the reviewed head passed hosted CI. The seven-tier checks cover alias resolution, projected launch settings and fake probes. The operator guide documents the external listener and activation limits.
<!-- SECTION:NOTES:END -->
