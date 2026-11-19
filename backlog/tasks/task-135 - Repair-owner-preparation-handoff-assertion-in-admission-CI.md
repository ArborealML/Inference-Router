---
id: TASK-135
title: Repair owner preparation handoff assertion in admission CI
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T07:54:00Z'
updated_date: '2026-11-19T08:04:00Z'
labels: []
dependencies: []
priority: high
ordinal: 133000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The failed assertion could observe the waiting request already holding the last slot. The corrected fixture observes availability before exact take and ownership after authorization. [Original hosted failure](https://github.com/ArborealML/Inference-Router/actions/runs/44482051276).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Deterministic observations distinguish an unreleased owner reservation from the joiner already authorized. Preserve exact winner, one send and one release.
- [x] #2 All affected owner-failure rows pass in focused Release and hosted build and test passes on the corrected PR head.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
All six affected Release cases passed locally on 819535e. The [corrected hosted run](https://github.com/ArborealML/Inference-Router/actions/runs/44483866089) passed on the same head. PR #8 retains its separate fixture review gate.
<!-- SECTION:NOTES:END -->
