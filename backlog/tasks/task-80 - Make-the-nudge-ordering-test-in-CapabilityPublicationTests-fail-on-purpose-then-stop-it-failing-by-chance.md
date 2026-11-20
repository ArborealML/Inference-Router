---
id: TASK-80
title: >-
  Make the nudge-ordering test in CapabilityPublicationTests fail on purpose,
  then stop it failing by chance
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T03:46:00Z'
updated_date: '2026-11-20T22:46:00Z'
labels:
  - concurrency
milestone: m-2
dependencies: []
priority: high
ordinal: 80000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Independent plan review will wake this task. Determine whether sampling before the second dispatch hint can explain the historical stale healthy result. Prove separately that the landed fixture detects delivery of a hint before the marker clears.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 With the second dispatch hint held, the historical assertion fails on the first switch’s healthy sample at the intended assertion.
- [ ] #2 Identify the sampled switch and show why the forced ordering yields the historical symptom. Record what the historical logs can establish.
- [ ] #3 At the same cut the acknowledged fixture stays pending. Release the exact second hint, observe its sample and complete the focused Release case successfully.
- [ ] #4 Reverse only marker-clear and hint delivery order with the qualified fixture unchanged. Observe the marker-specific failure, restore the exact source and pass the same focused case.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Earlier focused series observed 2/20 and 3/20 failures, and a later run at 3c83ecc failed on attempt eleven. The saved runs establish the symptom. The two controlled schedules replace the former repetition targets for this task; their results establish ordering only. All criteria remain open pending execution. Delivery owns bounded plan review before fixture work.
<!-- SECTION:NOTES:END -->
