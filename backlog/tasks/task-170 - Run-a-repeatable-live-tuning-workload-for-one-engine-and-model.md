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
Implement air tune run for one configured engine/model/exact recipe, reusing offline planner and comparison. Keep the first isolated reference run small; no remote-cluster prerequisite. Physical launch lanes and occupied concurrency are distinct.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Verify actual model, exact recipe, nominal window, physical lanes, tokenizer/template, hardware and exclusive admission before samples.
- [ ] #2 Freeze deterministic workload, body digests, exact rendered counts and enforced output allowance; include backend reserve and cache occupancy; failed preflight sends no workload.
- [ ] #3 Run near-window short waves and long-decode cells with measured overlap, actual generation length, TTFT, prefill/decode and end-to-end time; missing metrics remain unavailable.
- [ ] #4 Separate per-call bytes, cumulative bytes, calls and time for supported 1M-scale fitting. Verify final rendered count and unclamped allowance.
- [ ] #5 Record each request/wave, occupied concurrency, semantic terminal/usage, failures, resources and physical settlement. Timeout cannot establish vacancy.
- [ ] #6 Persist immutable versioned evidence, inconclusive and failed results; preserve offline comparison compatibility without granting qualification.
- [ ] #7 Qualify a real reference run on an authorized idle host; protect active user work and prove cleanup before subsequent runs.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Continue deterministic fitter and token-count adapter. Use an exclusive local verified admission path, even before manager extraction. Preserve near-window-text-v1 and normal-use-long-decode-v1 (65536 rendered input + 32768 output); backend reserve can make nominal 96K insufficient. Record cold load separately. Controlled fixtures first; amend SPEC §11 and tuning design with code/tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Existing focused offline fitter checks passed as previously recorded. Decision-81 governs reuse. Live executor and hardware qualification remain open.
<!-- SECTION:NOTES:END -->
