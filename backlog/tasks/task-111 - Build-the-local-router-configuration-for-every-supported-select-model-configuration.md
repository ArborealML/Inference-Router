---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T09:10:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI completion or an independent review verdict on PR #18 wakes the coordinator for landing or correction.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Project the literal per-tier command as a top-level Docker CMD-SHELL health check, with a 30-second interval and one-hour start period. Omit unset fields, check millisecond conversion for overflow, and preserve legacy omission. Reject malformed, misplaced and shadowed declarations. Keep health-only tiers behind the existing engine-wide startup and mutation guards. Compare all three complete GLM payloads across TOML and JSON, then run focused parser/refusal checks, config-only load and server Native AOT. Independent plan review dispatched on 0418440. DeepSeek timeout, retries and nested binds and the separate Strata profile alias stay outside.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The installed selector has 169 valid and 337 invalid spellings across 75 distinct targets (36 Strata, 26 HyperQwen, 13 Maya). Twelve spellings require one GPU; 22 defaults and 1,248 parking combinations were inventoried. Preserve GPU index, utilization, parking and suffix-cache limits in the tracked coverage report. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) landed offline per-target environment and mounts, strict ownership checks and single-pass substitution. [PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed per-tier RAM and swap for the Qwen profiles. [PR #16](https://github.com/ArborealML/Inference-Router/pull/16) at ee6e0f5 adds the MiniMax executable entrypoint and host IPC. Its 96 focused Release cases passed, the pinned MiniMax config loaded, and linux-x64 server Native AOT published. Independent code review dispatched. Remaining coverage includes profile aliases, GPU ordering, shared memory, runtime policy, nested mounts and health settings. Bind receipts and labels to canonical model, tier and serving instance before activation.

Single-device profiles require one explicit index and paired profiles two distinct indices. Utilization is 0.50–0.95 (default 0.90), parking 1–48 GiB (default 32), variable lanes 1–8. The selector has no supported suffix-cache launch.

PR #16 merged at 0418440 after hosted CI and independent PASS on ee6e0f5. The review confirmed the resource-free IPC payload, separate entrypoint and command, legacy omission, and refusal before mutation.

Plan review on 0418440 passed. Validate misplaced common probes in both formats explicitly, preserve unspecified Docker fields and distinguish default restart behavior from byte equivalence. The fingerprinted selector confirms the literal environment path. Offline implementation released.

[PR #18](https://github.com/ArborealML/Inference-Router/pull/18) passed 51 focused Release cases at 0b02a4e after correcting a test literal. The production-identical parent b422d61 loaded the pinned config and published Linux x64 Native AOT with zero trim warnings.
<!-- SECTION:NOTES:END -->
