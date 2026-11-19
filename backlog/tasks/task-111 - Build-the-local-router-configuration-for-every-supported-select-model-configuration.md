---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T10:05:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the offline Docker projection of the sole DeepSeek Maya-XS 32k tier on Defiant.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Plan review passed on 5643b27. Both changes are required for target 41 of 75. Project a checked positive health timeout and positive Int32 retries; omit unset fields. Permit only explicitly marked read-only tier children of an exact earlier read-only component ancestor. Validate all effective common-plus-tier destinations, preserve order, exclude annotations from Docker JSON and refuse common annotations, chains, writable or protected overlaps. Check nested raw JSON/TOML shadowing and direct DTO semantics, preserving legacy parsing and mutation gates. Compare one dual-format golden with fixed decimal 32768 arguments, 11 ordered mounts and unrelated writable logs. Reuse targeted mount, health-boundary and zero-write fixtures; run pinned config load and Native AOT once. Keep general DeepSeek command mapping, HyperQwen and live activation separate.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The installed selector has 169 valid and 337 invalid spellings across 75 distinct targets (36 Strata, 26 HyperQwen, 13 Maya). Twelve spellings require one GPU; 22 defaults and 1,248 parking combinations were inventoried. Preserve GPU index, utilization, parking and suffix-cache limits in the tracked coverage report. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) landed offline per-target environment and mounts, strict ownership checks and single-pass substitution. [PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed per-tier RAM and swap for the Qwen profiles. [PR #16](https://github.com/ArborealML/Inference-Router/pull/16) at ee6e0f5 adds the MiniMax executable entrypoint and host IPC. Its 96 focused cases, config load and Native AOT passed. Remaining coverage includes profile aliases, GPU ordering, shared memory, runtime policy, nested mounts and health settings. Bind receipts and labels to canonical model, tier and serving instance before activation.

Single-device profiles require one explicit index and paired profiles two distinct indices. Utilization is 0.50–0.95 (default 0.90), parking 1–48 GiB (default 32), variable lanes 1–8. The selector has no supported suffix-cache launch.

PR #16 merged at 0418440 after hosted CI and independent review.

[PR #18](https://github.com/ArborealML/Inference-Router/pull/18) projects the GLM health check offline. Review passed the supplied b422d61 production diff. The fixture correction 0b02a4e passed 51 focused Release cases. Parent config load and zero-trim Linux x64 Native AOT cover unchanged production. The fixture review confirmed distinct TOML keys. All hosted checks passed and PR #18 merged at 989ae12. Before live qualification verify the image shell and router readiness budget independently of Docker’s start period.

[PR #21](https://github.com/ArborealML/Inference-Router/pull/21) at 6892695 passed 52 focused Release cases, the pinned Strata config load and zero-warning Linux x64 Native AOT. Merged at 5643b27 after hosted CI and static review passed. The hosted tree matched qualification; optional spelling permutations remain covered by general unknown-tier refusal.
<!-- SECTION:NOTES:END -->
