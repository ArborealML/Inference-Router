---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T07:57:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Independent plan review of MiniMax 32k entrypoint and IPC wakes the next offline implementation. Build the local configuration from the installed selector and verify all valid models, aliases, profiles, defaults and options. Decision-68 authorizes local qualification; TASK-125 carries the first live pair.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
For one dedicated MiniMax engine, project a literal absolute executable into Docker Entrypoint, preserve the separate Cmd array and project explicit host IPC into HostConfig.IpcMode. IPC alone needs HostConfig; entrypoint alone does not. Detect both options before raw ancestry and authority fields can be shadowed. Preserve absent-field behavior and keep each option behind startup and pre-mutation gates for every model on that engine. Compare the exact serving payload through fake projection, with isolated options, invalid input and zero-write controls. Verify the private config with the loader. Keep other selector gaps independent.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Selector inventory at fingerprint 8af827d3c375efda08cad8a5b9b7fd862b44a2f5a7482042c5255623a891a030 found 169 valid and 337 invalid spellings, 75 distinct targets, 12 spellings requiring one explicit GPU, 22 omitted defaults, and 1,248 parking combinations. Single-device profiles require one explicit index; paired profiles require two distinct indices. GPU utilization accepts 0.50–0.95, default 0.90. Parking accepts 1–48 GiB, default 32, and 1–8 variable lanes. Suffix-cache launch has no supported mechanism. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) corrects shadowed ownership and selector fields, common Docker key casing and template substitution. Review of 7eda8ad accepted the correction subject to verification. At 6505ea1 all 108 focused tests, the pinned pair config and server Native AOT passed. The CLI separately refused a sibling select/Select pair and a duplicate common env key at the expected paths. Integration head 3af5f10 preserves both SPEC revisions. Continue offline comparison for remaining targets. Before activation, bind receipts and labels to the same canonical model, tier and serving instance.

Offline comparison covered 36 Strata, 26 HyperQwen and 13 Maya targets. MiniMax 32k requires host IPC and an entrypoint override. Qwen 768k and 1m require profile-specific memory and total swap. At 606961f all 127 focused Docker target/resource tests passed. The pinned Defiant config loaded and the linux-x64 server Native AOT publish had zero trim warnings.  Other gaps include profile aliases, GPU ordering, shared memory, runtime policy, nested mounts and health settings.

[PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed at 5992c4e with SPEC v0.12.78 after hosted Release, Native AOT and smoke passed on 7133401. Review accepted supplied-profile projection. Config-only controls accepted either common RAM key spelling alone and refused both together as duplicate. Automatic profile selection and live activation remain separate.

MiniMax 32k offline IPC and entrypoint plan review dispatched against 5992c4e on November 19.
<!-- SECTION:NOTES:END -->
