---
id: TASK-171
title: Search bounded configurations and recommend a measured winner for one model
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T09:21:00Z'
labels: []
milestone: m-14
dependencies:
  - TASK-170
references:
  - docs/hardware-tuning-design.md
ordinal: 166000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use the one runner for progressive context/launch search. Preserve the earlier 262144 singleton TTFT experiment as a workload-specific objective; normal ladder search maximizes measured end-to-end aggregate completed-output throughput subject to stated service floors.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ascend 64K, 96K, 128K, 196K, 256K, 512K, 768K, 1024K (K=1024); skip unsupported tiers with reason.
- [ ] #2 Test consecutive occupied concurrency from one; cap next supported tier by preceding measured optimum, not maximum stable. Unsupported tiers retain last valid cap; explicit exhaustive override widens search.
- [ ] #3 Record physical lanes separately; new physical recipes above the normal cap require visible override. Distinct effective recipes remain distinct.
- [ ] #4 Compare identical work at occupancy one/full and a partial point when cap exceeds two; retain passing points. Different near-window workloads are not comparable as identical work.
- [ ] #5 Record transition/cache cost and service floors; stop propagation on no passing optimum, identity drift, inconclusive instrumentation or unresolved settlement.
- [ ] #6 Report passed, failed, unsupported, inconclusive and untested separately; retain safely settled lower passing evidence. Recommendations never auto-apply.
- [ ] #7 Retain singleton long-prompt TTFT experiment and resource observations including disk reads, page faults and memory/GPU where available; insufficient context cannot win.
- [ ] #8 SPEC §11 and tuning design specify objective, cap, overrides and evidence; deterministic fixtures verify propagation and refusal.
- [ ] #9 Deterministic ties prefer lower occupied concurrency within service floors; fixture search uses synthetic runner results and does not require a live hardware trial.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Enumerate finite exact recipes and occupancy through TASK-170. Measure end-to-end completed-output throughput under fixed work/service floors; use smaller occupancy on indistinguishable ties. Use matched workloads for runtime decisions. c=1 in P=8 is not P=1. Test all eight binary values and skipped/no-winner/override propagation. TASK-154 owns actual measurements; TASK-216 owns adoption mechanics.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Preserve decisions 74/81. Progressive cap is a search heuristic, not a theorem.
<!-- SECTION:NOTES:END -->
