---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T12:09:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent parser correction review dispatched November 19 wakes the coordinator. At 6ea04f8 eight misplaced or shadowed model-level Docker policy inputs unexpectedly loaded and two valid controls passed. At 0d76e2d all ten passed, zero skips, and the pinned paired config loaded. Parent qualification was 63/63 plus warning-free Native AOT. Future activation requires a known daemon driver supporting both rotation options. Landed offline coverage remains 49/75 after [PR #24](https://github.com/ArborealML/Inference-Router/pull/24).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Separate selector locator, named digest, platform manifest, config digest, daemon-local identity and platform by evidence source. Mark missing bindings unavailable; synthetic fixture identities must be explicit. Preserve four sequences per replica without claiming global capacity. Project common Docker init, unless-stopped restart and log rotation with the daemon-selected driver. On this gated projection derive ExposedPorts from the existing published port. Pin four paired-device tiers on Swift and Classic with one independent payload, eight row comparisons, option-only refusal, raw/DTO validation and a policy-only digest change. Preserve legacy payloads and zero writes. Qualify the config and AOT once. Image association, restart authority, physical admission and same-wire-ID process binding remain activation dependencies. Six single-GPU and twelve parking targets remain separate.
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
