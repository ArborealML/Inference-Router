---
id: TASK-172
title: Sweep the configured catalog with air tune run --all-models and --resume
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-171
references:
  - docs/hardware-tuning-design.md
ordinal: 167000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Run the same single-model search over canonical engine/model pairs, retaining per-host results and resumable compatible evidence. Do not gate the first useful runner on a catalog sweep.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Enumerate canonical pairs once; aliases do not repeat; multiple engines retain distinct results.
- [ ] #2 Serialize shared-GPU trials; independent domains may progress. Save settled outcomes and protect serving.
- [ ] #3 Resume matching settled cells only, keyed by workload, hardware, weights, runtime and recipe; reconcile interrupted ambiguity before reopening the affected domain.
- [ ] #4 Report all measured alternatives, stable bound, optimum, skipped/untested and unmet constraints; targeted invocation yields the same score.
- [ ] #5 SPEC §11 and fixtures cover interruption, changed identity and unresolved cleanup. Resume cannot silently adopt evidence.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TASK-171 search without a second runner. Keep raw records. Continue independent safe domains after a settled failure.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decision-81 remains authoritative.
<!-- SECTION:NOTES:END -->
