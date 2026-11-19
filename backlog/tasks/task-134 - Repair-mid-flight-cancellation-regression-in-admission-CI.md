---
id: TASK-134
title: Repair mid-flight cancellation regression in admission CI
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-19T07:11:00Z'
labels: []
dependencies: []
priority: high
ordinal: 132000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted build and test at 7477063 failed TicketLifetimeTests.AbortingTheCallerMidFlightCancelsUpstreamAndReleasesCapacity line 147: expected one cancelled upstream completion, observed zero. The test waits for logical inflight before cancellation. Establish whether cancellation precedes actual handler entry or an entered request fails to observe it. [Failed CI](https://github.com/ArborealML/Inference-Router/actions/runs/44477048212).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A deterministic held upstream request observes cancellation once, releases capacity and produces one terminal event. Preserve pre-send cancellation behavior.
- [ ] #2 TicketLifetimeTests passes locally on the correction and the hosted build and test job passes on the corrected PR head.
<!-- AC:END -->
