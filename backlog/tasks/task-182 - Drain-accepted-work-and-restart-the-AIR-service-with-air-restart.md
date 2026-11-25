---
id: TASK-182
title: Drain accepted work and restart the AIR service with air restart
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T00:25:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 174000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Replace external status polling and stop/start scripts with a coordinated service drain and restart.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Allow queued work, active streams and model switches to finish. Coordinate admission with draining so new arrivals receive a retryable response.
- [ ] #2 Validate the configuration before shutdown, start the replacement with the same configuration, and report its readiness.
- [ ] #3 Cancellation before shutdown restores admission. Concurrent restart commands cannot initiate overlapping restarts.
<!-- AC:END -->
