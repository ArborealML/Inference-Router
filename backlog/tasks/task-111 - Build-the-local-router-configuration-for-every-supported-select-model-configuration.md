---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T09:19:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent plan verdict wakes configuration work on six Strata profile spellings.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review dispatched on the landed GLM source at 989ae12. Resolve max-4 to canonical 256k-4 for three Strata models and two model spellings each. Validate ordinal per-model aliases, collisions, hidden declarations and canonical-only defaults. Reuse one frozen plan and preserve create bytes, labels, defaults and the 75 normalized targets. Discriminate equal gauges with different settings. Coordinate the captured-tier lookup in verified dispatch in actual merge order. Preserve existing startup and activation gates.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The installed selector has 169 valid and 337 invalid spellings across 75 distinct targets (36 Strata, 26 HyperQwen, 13 Maya). Twelve spellings require one GPU; 22 defaults and 1,248 parking combinations were inventoried. Preserve GPU index, utilization, parking and suffix-cache limits in the tracked coverage report. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) landed offline per-target environment and mounts, strict ownership checks and single-pass substitution. [PR #12](https://github.com/ArborealML/Inference-Router/pull/12) landed per-tier RAM and swap for the Qwen profiles. [PR #16](https://github.com/ArborealML/Inference-Router/pull/16) at ee6e0f5 adds the MiniMax executable entrypoint and host IPC. Its 96 focused Release cases passed, the pinned MiniMax config loaded, and linux-x64 server Native AOT published. Independent code review dispatched. Remaining coverage includes profile aliases, GPU ordering, shared memory, runtime policy, nested mounts and health settings. Bind receipts and labels to canonical model, tier and serving instance before activation.

Single-device profiles require one explicit index and paired profiles two distinct indices. Utilization is 0.50–0.95 (default 0.90), parking 1–48 GiB (default 32), variable lanes 1–8. The selector has no supported suffix-cache launch.

PR #16 merged at 0418440 after hosted CI and independent review.

[PR #18](https://github.com/ArborealML/Inference-Router/pull/18) projects the GLM health check offline. Review passed the supplied b422d61 production diff. GitHub now serves that exact parent. Its fixture failed compilation; the published one-line child 0b02a4e passed 51 focused Release cases. Parent config load and zero-trim Linux x64 Native AOT cover unchanged production. The fixture review confirmed distinct TOML keys. All hosted checks passed and PR #18 merged at 989ae12. Before live qualification verify the image shell and router readiness budget independently of Docker’s start period.
<!-- SECTION:NOTES:END -->
