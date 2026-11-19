---
id: TASK-135
title: Repair owner preparation handoff assertion in admission CI
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-19T07:54:00Z'
labels: []
dependencies: []
priority: high
ordinal: 133000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PR #8 build and test failed on bfc9767. FailedStagedOwnerPreparationReleasesTheParkedJoiner (throws=true, afterArm=null) at ExactTicketAdmissionTests.cs line 951 observed CanJoinerTake false after failed preparation. Determine whether the live joiner already consumed the slot or owner release ordering is incorrect. [Failed hosted run](https://github.com/ArborealML/Inference-Router/actions/runs/44482051276).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Deterministic observations distinguish an unreleased owner reservation from the joiner already authorized. Preserve exact winner, one send and one release.
- [ ] #2 All affected owner-failure rows pass in focused Release and hosted build and test passes on the corrected PR head.
<!-- AC:END -->
