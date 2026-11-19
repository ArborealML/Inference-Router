---
id: TASK-134
title: Repair mid-flight cancellation regression in admission CI
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T07:11:00Z'
updated_date: '2026-11-19T07:23:00Z'
labels: []
dependencies: []
priority: high
ordinal: 132000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The failed fixture cancelled on logical inflight, before the upstream handler necessarily entered. PR #5 now distinguishes cancellation before send from cancellation after handler registration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A deterministic held upstream request observes cancellation once, releases capacity and produces one terminal event. Preserve pre-send cancellation behavior.
- [x] #2 TicketLifetimeTests passes locally on the correction and the hosted build and test job passes on the corrected PR head.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At 374af08 all six TicketLifetimeTests passed in Release. Before send there were zero handler calls. After entry the handler observed one cancellation. Both returned capacity with one terminal event. [Corrected hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44478801107) passed the full tests, Native AOT and smoke check. [Original failure](https://github.com/ArborealML/Inference-Router/actions/runs/44477048212). The independent fixture review is tracked with PR #5.
<!-- SECTION:NOTES:END -->
