---
id: TASK-144
title: Plan hardware-specific qualification of stepped inference tiers
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-20T00:33:00Z'
updated_date: '2026-11-20T18:12:00Z'
labels: []
milestone: m-14
dependencies: []
ordinal: 142000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The [hardware tuning design](https://github.com/ArborealML/Inference-Router/blob/dcfdf15dbf88db72fa4d52812319ab87c4ec96a3/docs/hardware-tuning-design.md) defines the proposed CLI, bounded workloads, concurrency and throughput measurements, and matching rules for hardware comparisons. Its 64 GB and 128 GB example is hypothetical. Implementation and hardware measurements are future work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Propose a repeatable hardware-specific sweep recording model, runtime, GPU allocation, memory limits, requested and observed context, concurrency, and success or failure.
- [x] #2 Define how measured results inform engine TOML and profile selection, including safe handling of failed or untested tiers and selection of normal defaults at 96k or higher where qualified, with smaller safe tiers explicit.
- [x] #3 Define CLI inputs, a reproducible load near each target window, stability criteria, repetition and a search bound. Report highest demonstrated stable concurrency and distinguish measured failures from untested limits.
- [x] #4 For each window, distinguish peak aggregate output tokens per second from single-stream decode speed. Record winning concurrency, prompt processing speed and time to first token with the workload and samples.
- [x] #5 Save separate comparable runs with physical RAM, effective memory and swap limits, CPU, assigned GPU identities and VRAM, model and runtime identity, effective settings and workload. Demonstrate comparing 64 GB and 128 GB configurations.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Map existing CLI and engine configuration to a proposed hardware-specific tuning command. Define the workload, concurrency search, stopping rules, speed measurements and comparable hardware records. Submit the plan for review before implementation or measurement.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review accepted the corrected design at ab891ba. [PR #51](https://github.com/ArborealML/Inference-Router/pull/51) landed at dcfdf15. [Post-merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44789597363) passed Release, Native AOT and smoke checks.
<!-- SECTION:NOTES:END -->
