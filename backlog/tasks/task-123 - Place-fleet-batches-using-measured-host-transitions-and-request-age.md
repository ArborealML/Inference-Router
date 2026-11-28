---
id: TASK-123
title: Place fleet batches using measured host transitions and request age
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-122
  - TASK-71
  - TASK-110
ordinal: 121000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement measured fleet placement and batching against fixed-placement contracts and capture fixtures; enable live adaptive placement only after fixed-placement qualification. The router chooses the eligible host/model/cohort; each host retains authoritative physical admission and lifecycle. Keep fixed-placement fallback when measurements are missing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 On the same arrivals, changing only measured transition times changes a host or batch choice. A host refusal excludes that candidate even when it has the cheapest estimate.
- [ ] #2 With bounded work and continuing arrivals, an eligible aged request completes within the stated bound. A lost heartbeat never counts as a vacant host.
- [ ] #3 Priority changes only competing queue order, not economic score, context, lanes or spending; a held host does not hide eligible work elsewhere.
- [ ] #4 Measured transition, cache and actual finite demand inform placement; uncertain acceptance and host refusal cannot be overridden.
- [ ] #5 Before live adaptive placement, record authorized representative real workload and comparable transition/cache/settlement/service evidence; qualify enabled modes against fixed placement and bounded-load fairness, or keep adaptation disabled.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Offline scoring/replay can precede live TASK-124/72 qualification; the TASK-123 assignee owns the actual adaptive-mode evidence, not a duplicate tuning runner. Reuse bounded-load fairness/deadline policy; do not enable live adaptive placement until those gates and service bounds are qualified.
<!-- SECTION:NOTES:END -->
