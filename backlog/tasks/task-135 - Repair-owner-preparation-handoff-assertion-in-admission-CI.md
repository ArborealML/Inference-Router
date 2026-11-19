---
id: TASK-135
title: Repair owner preparation handoff assertion in admission CI
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T07:54:00Z'
updated_date: '2026-11-19T07:59:00Z'
labels: []
dependencies: []
priority: high
ordinal: 133000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI on the corrected PR head wakes closure. The previous mid-dispatch availability assertion could observe the waiting request already holding the last slot. [Failed hosted run](https://github.com/ArborealML/Inference-Router/actions/runs/44482051276).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Deterministic observations distinguish an unreleased owner reservation from the joiner already authorized. Preserve exact winner, one send and one release.
- [ ] #2 All affected owner-failure rows pass in focused Release and hosted build and test passes on the corrected PR head.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At 819535e all six affected Release cases passed. The fixture pauses before exact take to observe free capacity, then before send to observe the selected joiner holding that slot. Existing exact-body, single send and release checks stand. Independent fixture review dispatched.
<!-- SECTION:NOTES:END -->
