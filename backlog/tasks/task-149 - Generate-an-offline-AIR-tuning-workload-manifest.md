---
id: TASK-149
title: Generate an offline AIR tuning workload manifest
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T22:44:00Z'
updated_date: '2026-11-21T00:11:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-144
  - TASK-146
references:
  - docs/hardware-tuning-design.md
priority: high
ordinal: 147000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The offline workload planner landed in [PR #68](https://github.com/ArborealML/Inference-Router/pull/68) at 6597024. It generates a deterministic manifest from configured models, tiers and slots. All three [post-merge checks](https://github.com/ArborealML/Inference-Router/actions/runs/44841535679) passed. Hardware capacity and performance require measured workloads.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The plan verb accepts the reviewed options, resolves one engine and canonical model or alias, and selects one exact tier or all eligible tiers in deterministic order.
- [x] #2 Strict UTF-8 configuration input admits at most 65,536 bytes. Invalid input produces the specified exit status, fixed redacted stderr and empty stdout before any report is written.
- [x] #3 The versioned JSON manifest reproduces the independent generator, warmup and grid vectors, respects tier and slot limits and serializes at most 1,744 coordinates within 524,288 bytes including its final LF.
- [x] #4 Equivalent TOML and JSON configurations, including alias and canonical requests, yield identical golden bytes in focused Release tests and through the published Linux x64 Native AOT CLI with zero trim warnings.
- [x] #5 README, tuning guide and SPEC document the CLI, output and distinction between configured intent and measured hardware capacity.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement air tune plan --config FILE --engine ENGINE --model MODEL --seed HEX --max-concurrency N [--profile TIER] with CLI-local source-generated DTOs, the existing owned-descriptor reader and config loader. Reject duplicate options, case-twin engines, ambiguous aliases, BOM, malformed UTF-8 and overflow. Require 64 lowercase hex seed characters and concurrency 1–8. Select an explicit ordinal tier or sort 1–16 tiers by window and name. Emit compact air_tune_workload_plan v1 with final LF. Record configured limits, min(N, slots), the window preview, 512 output tokens, warmup and three waves per level. Use the reviewed embedded generator, byte framing, checked arithmetic and separate warmup seed. Preserve the existing comparison contract. Exercise the focused AirTuneWorkloadPlanOffline cases, independent vectors, 671/672 and 16/17 tier boundaries, all seven HY4 tiers, redaction, file overflow, maximum writer size and the published CLI golden. Update the user-facing docs and SPEC §11. The reviewed count budget, failed-count termination and paired warmup replays are future executor contracts.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The last-value correction passed independent review on bc646c0. All 27 focused Release cases passed, including shadowed-null acceptance and effective-null rejection. The Linux x64 Native AOT CLI emitted no trim warnings and matched the independent 5,510-byte golden with empty stderr. The full-tail control is preserved. These results qualify offline manifest generation.
<!-- SECTION:NOTES:END -->
