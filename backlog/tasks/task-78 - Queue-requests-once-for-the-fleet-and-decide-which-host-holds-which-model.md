---
id: TASK-78
title: Queue requests once for the fleet and decide which host holds which model
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T20:55:00Z'
labels:
  - scheduling
milestone: m-6
dependencies:
  - TASK-77
  - TASK-69
priority: high
ordinal: 78000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fleet delivery umbrella: one authoritative router owns pending work, eligibility and placement; each managed host owns physical admission and lifecycle. First qualify static placement and independent cross-provider progress. Later choose placements and batches from measured costs without requiring full autonomous tuning.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All cluster ingress reaches one authoritative pending queue and ticket ownership model, without a second independent scheduler.
- [ ] #2 Fixed host/model placement and managed peer activation work independently of autonomous placement and catalog tuning.
- [ ] #3 Held lifecycle, capacity and refusal on one resource domain do not prevent another admissible provider dispatching.
- [ ] #4 Each host can veto placement; lost heartbeat or uncertain acceptance never establishes vacancy or permits replay.
- [ ] #5 Measured placement later accounts for transitions, cache, bounded wait and eligibility; priority remains queue-only.
- [ ] #6 SPEC §§4–7, 12, 14 describe contracts and qualification stages in the owning implementation changes.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At 31179e3 one live ingress served resident Qwen and a remote resident Bonsai in one Pi session. That is not managed peer ownership or two-ingress recovery proof. TASK-203/204 establish independent dispatch and identities; TASK-206 extracts the physical manager; TASK-77 finishes remote ownership; TASK-122 and TASK-124 qualify fixed placement and recovery before TASK-123 adds measured placement. Preserve decision-25 bounded wait and decision-67 physical cleanup.

Outcome rollup only: close after the pinned TASK-122/124 fixed-placement and TASK-123 adaptive-placement evidence plus TASK-72 fairness. Do not dispatch as a separate implementation or duplicate child criteria. Coordinator reviews the aggregate evidence.
<!-- SECTION:NOTES:END -->
