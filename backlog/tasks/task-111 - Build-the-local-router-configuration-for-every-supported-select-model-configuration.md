---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T08:26:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI and independent review of the MiniMax 32k launch options wake integration. Per-target Docker settings and tier memory have landed. Complete native configuration parity spans 75 selector targets.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Add engine-wide entrypoint and ipc_mode. Encode one literal clean absolute in-image Linux executable in Docker Entrypoint; preserve Cmd separately. Validate path segments, controls, slashes and whitespace without host lookup or template expansion. Only exact host IPC qualifies for offline projection. IPC alone creates HostConfig; entrypoint alone does not. Omit both for legacy payloads. Detect either option before deserialization, including shadowed ancestors, casing, null and ownership/selector conflicts. Validate direct DTO input. Limit strictness to opted engines. Refuse startup before binding any default, injected or enrolled provider. For provider mutation preserve target refusal first, then launch-options refusal, resources, then image/daemon checks. Test each option alone across the engine. Reuse fake payload and refusal fixtures, the redacted MiniMax payload, a private config-only load and one server Native AOT check.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The installed selector has 169 valid and 337 invalid spellings across 75 distinct targets (36 Strata, 26 HyperQwen, 13 Maya). Twelve spellings require one GPU; 22 defaults and 1,248 parking combinations were inventoried. Preserve GPU index, utilization, parking and suffix-cache limits in the tracked coverage report. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) landed offline per-target environment and mounts, strict ownership checks and single-pass substitution. [PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed per-tier RAM and swap for the Qwen profiles. [PR #16](https://github.com/ArborealML/Inference-Router/pull/16) at ee6e0f5 adds the MiniMax executable entrypoint and host IPC. Its 96 focused Release cases passed, the pinned MiniMax config loaded, and linux-x64 server Native AOT published. Independent code review dispatched. Remaining coverage includes profile aliases, GPU ordering, shared memory, runtime policy, nested mounts and health settings. Bind receipts and labels to canonical model, tier and serving instance before activation.

Single-device profiles require one explicit index and paired profiles two distinct indices. Utilization is 0.50–0.95 (default 0.90), parking 1–48 GiB (default 32), variable lanes 1–8. The selector has no supported suffix-cache launch.
<!-- SECTION:NOTES:END -->
