---
id: TASK-170
title: Run a repeatable live tuning workload for one engine and model
status: In Progress
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
updated_date: '2026-11-23T22:01:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-149
  - TASK-146
references:
  - docs/hardware-tuning-design.md
ordinal: 165000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement air tune run for one configured engine, model and profile. Reuse the offline planner and comparison format. Use Hy4 for the first live reference run.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Resolve aliases and verify actual backend model, context and slots before accepting samples. Record hardware, runtime, effective launch settings and workload identity.
- [ ] #2 Run a repeatable short workload and a representative long prompt. Record cold-load time, time to first token, prefill tokens/sec and decode tokens/sec separately, with token counts and cache conditions.
- [ ] #3 Persist successes and failures. Two compatible saved runs can be compared with air tune compare, with long-prompt measurements retained separately from the short grid.
- [ ] #4 Verify the runner against native Hy4 measurements on an idle host and preserve active user requests.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Resolve one configured engine, canonical model and named tier through the existing workload planner. Verify the intended endpoint, resident profile and idle measurement scope before any workload. Run deterministic short and representative long prompts using bounded streams. Separate cold-load evidence, first output latency, native cache-adjusted prefill and measured decode. Persist terminal and inconclusive results with compatible comparison evidence. Exercise the normal CLI against a loopback serving fixture before qualifying Hy4 on an idle host.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The deterministic prompt fitter is implemented with a call budget and an exact byte limit. Two focused offline checks passed. Next verify the Maya token-count adapter, then connect the runner to admission and residency evidence. A cold-load measurement requires an observed launch. Live qualification requires exclusive admission on an idle host.
<!-- SECTION:NOTES:END -->
