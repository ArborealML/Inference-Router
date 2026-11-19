---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T13:36:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent selector comparison plan review dispatched November 19 wakes the coordinator. All 75 normalized model and tier defaults have landed across nine models, including twelve parking defaults in [PR #30](https://github.com/ArborealML/Inference-Router/pull/30). The dedicated-engine topology guard requires six native configurations. Both remaining criteria stay open. Public shared-memory inputs and the tier DTO reject explicit null; the existing common DTO treats direct null assignment as omission.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review the smallest defensible offline comparison of actual Docker create payloads with independent selector expectations. Proposed batch covers 10,148 projections including utilization spelling, cache, lane and ordered device choices. Assess factoring independent dimensions and structural evidence for continuous utilization. Preserve explicit router ownership and identify credential mount, published port, per-option reconfiguration and host qualification gaps in the report before tooling.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The installed selector has 169 valid and 337 invalid spellings across 75 distinct targets (36 Strata, 26 HyperQwen, 13 Maya). Twelve spellings require one GPU; 22 defaults and 1,248 parking combinations were inventoried. Preserve GPU index, utilization, parking and suffix-cache limits in the tracked coverage report. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) landed offline per-target environment and mounts, strict ownership checks and single-pass substitution. [PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed per-tier RAM and swap for the Qwen profiles. [PR #16](https://github.com/ArborealML/Inference-Router/pull/16) at ee6e0f5 adds the MiniMax executable entrypoint and host IPC. Its 96 focused cases, config load and Native AOT passed. Bind receipts and labels to canonical model, tier and serving instance before activation.

Single-device profiles require one explicit index and paired profiles two distinct indices. Utilization is 0.50–0.95 (default 0.90), parking 1–48 GiB (default 32), variable lanes 1–8. The selector has no supported suffix-cache launch.

[PR #18](https://github.com/ArborealML/Inference-Router/pull/18) projects the GLM health check offline. Review passed the supplied b422d61 production diff. The corrected fixture passed 51 Release cases; config and Native AOT passed. All hosted checks passed and PR #18 merged at 989ae12. Before live qualification verify the image shell and router readiness budget independently of Docker’s start period.

[PR #21](https://github.com/ArborealML/Inference-Router/pull/21) at 6892695 passed 52 focused Release cases, the pinned Strata config load and zero-warning Linux x64 Native AOT. Merged at 5643b27 after hosted CI and static review passed. The hosted tree matched qualification; optional spelling permutations remain covered by general unknown-tier refusal.

[PR #22](https://github.com/ArborealML/Inference-Router/pull/22) at edc8b1a passed 69 focused Release cases, pinned config-only load and warning-free Linux x64 Native AOT. Hosted CI passed. Review counterexample disproved: CLI accepted a clean second tier and rejected both misplaced annotation casings before launch. Merged at 0abdbf5; offline coverage reached 41/75 at that landing.
<!-- SECTION:NOTES:END -->
