---
id: TASK-170
title: Run a repeatable live tuning workload for one engine and model
status: In Progress
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
updated_date: '2026-11-27'
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
Implement air tune run for one configured engine/model/exact recipe using the existing planner and comparator. Complete deterministic runner/measurement fixtures without live hardware access; TASK-154 owns authorized real reference measurements. Physical lanes and occupied concurrency are distinct.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Verify exact model/recipe, window, physical lanes, tokenizer/template, hardware and exclusive admission before samples; controlled fixtures include refusal.
- [ ] #2 Freeze deterministic workload, body digests, rendered counts and enforced output allowance; include reserve/cache occupancy. Failed preflight sends no workload.
- [ ] #3 Run near-window short waves and long-decode cells; measure overlap, generated length, TTFT, prefill/decode and end-to-end time; missing metrics remain unavailable.
- [ ] #4 Separate per-call bytes, cumulative bytes, calls and time for supported 1M-scale fitting; verify final rendered count and unclamped allowance.
- [ ] #5 Record each request/wave, occupied concurrency, semantic outcome/usage, failures, resources and physical settlement; timeout cannot prove vacancy.
- [ ] #6 Persist versioned immutable evidence, failures and inconclusive results; retain offline comparison compatibility without granting qualification.
- [ ] #7 Deterministic capture and synthetic measurement fixtures exercise exclusive admission, stop on unproved settlement, and bound workloads; actual hardware qualification belongs to the measurement workstream.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Continue deterministic fitter/token-count adapter. Reuse existing exclusive local admission and TASK-187's semantic outcome contract; no remote manager or commercial prerequisite. Preserve near-window-text-v1 and normal-use-long-decode-v1 (65536 rendered input + 32768 output); reserve can make 96K insufficient. Record cold load separately. Implement against capture fixtures before an authorized real run. Amend SPEC §11 and tuning design with code/tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Existing focused offline fitter checks passed as previously recorded; decision-81 governs reuse. Reference real Hy4 run belongs to TASK-154. Refresh worker plan and status after reconciliation; no live run claimed.
<!-- SECTION:NOTES:END -->
