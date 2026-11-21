---
id: TASK-80
title: >-
  Make the nudge-ordering test in CapabilityPublicationTests fail on purpose,
  then stop it failing by chance
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T03:46:00Z'
updated_date: '2026-11-20T23:57:00Z'
labels:
  - concurrency
milestone: m-2
dependencies: []
priority: high
ordinal: 80000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on explicit qualification release after first-swap work. Independent rereview passed the marker-order criterion wording. Preserve the separate held-second-hint test and its pending acknowledgment. All four criteria await execution.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 With the second dispatch hint held, the historical assertion fails on the first switch’s healthy sample at the intended assertion.
- [ ] #2 Identify the sampled switch and show why the forced ordering yields the historical symptom. Record what the historical logs can establish.
- [ ] #3 At the same cut the acknowledged fixture stays pending. Release the exact second hint, observe its sample and complete the focused Release case successfully.
- [ ] #4 Move the single hint before marker clear in a temporary mutation, inside the gate and before Ready. With the fixture unchanged, verify eventual readiness and acknowledgment, first Assert.Null failure showing the marker, and green after exact restoration.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Earlier focused series observed 2/20 and 3/20 failures, and a later run at 3c83ecc failed on attempt eleven. Those logs establish the symptom. The temporary early callback runs inside the transition gate before marker clear and readiness. The later acknowledgment establishes completion only. Qualification uses the two controlled schedules and makes no historical incidence claim.
<!-- SECTION:NOTES:END -->
