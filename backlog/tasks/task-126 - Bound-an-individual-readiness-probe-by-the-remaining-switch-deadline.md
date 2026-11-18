---
id: TASK-126
title: Bound an individual readiness probe by the remaining switch deadline
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-18T22:58:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 124000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The readiness loop checks its deadline before calling the state and model endpoints. A pending HTTP call can outlive the remaining budget. Preserve external cancellation and fail the attempt through its existing terminal policy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hold a state probe and then a models probe separately past the remaining switch budget. Each attempt settles within the named bound and produces one terminal result.
- [ ] #2 A delayed successful reply after timeout cannot publish readiness or revive a retired attempt. A healthy alternate can still make progress.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one monotonic readiness budget across state headers and body, model discovery, and poll delay. Separate observation from publication so a late or cancellation-ignoring handler cannot publish gauges or readiness after terminal settlement. Preserve shutdown precedence and the last completed mismatch diagnosis. Use bounded held handlers and a controllable clock to exercise each wait, cancellation races and alternate recovery. Coordinate with TASK-68 on snapshot ownership.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Pre-implementation review dispatched. The coordinator will verify the failure on the current path and run the integrated regression fixture.

Review found that state probes write gauges internally and a deadline checked only before a fetch can lose at publication. The existing clock hook covers waiter deadlines only. Check one monotonic readiness deadline under the publication guard; late observations may dispose resources but cannot change gauges, events or the winner.

The standalone deadline helper and expanded fixtures stopped at two disposal analyzers before any tests ran. The implementer owns the correction. Legacy and enrolled call sites follow the observation-only readers.
<!-- SECTION:NOTES:END -->
