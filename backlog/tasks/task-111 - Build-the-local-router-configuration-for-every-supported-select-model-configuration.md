---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T09:15:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Prepare the Strata max-4 alias plan covering six selector spellings across three existing targets. The three offline GLM health checks are on main.
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

PR #16 merged at 0418440 after hosted CI and independent review.

[PR #18](https://github.com/ArborealML/Inference-Router/pull/18) projects the GLM health check offline. Review passed the supplied b422d61 production diff. GitHub now serves that exact parent. Its fixture failed compilation; the published one-line child 0b02a4e passed 51 focused Release cases. Parent config load and zero-trim Linux x64 Native AOT cover unchanged production. The fixture review confirmed distinct TOML keys. All hosted checks passed and PR #18 merged at 989ae12. Before live qualification verify the image shell and router readiness budget independently of Docker’s start period.
<!-- SECTION:NOTES:END -->
