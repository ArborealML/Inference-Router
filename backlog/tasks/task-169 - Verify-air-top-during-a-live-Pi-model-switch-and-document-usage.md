---
id: TASK-169
title: Verify air top during a live Pi model switch and document usage
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:10:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-168
references:
  - README.md
ordinal: 164000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Exercise the installed monitor with ordinary Pi traffic and publish usage and observed behavior.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 While one request finishes on its resident model, the original queued request for another configured model drives the switch and streams without an admin switch.
- [ ] #2 The monitor follows that ticket through dispatch and the reported residency change agrees with an independent backend readback.
- [ ] #3 Opening and quitting the monitor leave active requests intact. README documents the command, keys and stale-data indicators.
<!-- AC:END -->
