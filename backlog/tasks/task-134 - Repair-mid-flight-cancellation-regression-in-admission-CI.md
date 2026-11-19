---
id: TASK-134
title: Repair mid-flight cancellation regression in admission CI
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T07:11:00Z'
updated_date: '2026-11-19T07:31:00Z'
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
At 374af08 six focused Release cases and [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44478801107) passed. [PR #5](https://github.com/ArborealML/Inference-Router/pull/5) landed at 1061d70. Review accepted the fixture for the original cancellation regression. The inline selection barrier observes zero fake-handler entries before scheduling; the entered handler observes one cancellation. Both observe one terminal and engine inflight returning to zero. They do not count release calls, prove thread quiescence or cover the interval after forwarding authorization. Broader accounting belongs to admission and physical retirement.
<!-- SECTION:NOTES:END -->
