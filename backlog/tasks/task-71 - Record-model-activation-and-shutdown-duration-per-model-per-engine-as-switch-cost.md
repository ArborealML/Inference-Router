---
id: TASK-71
title: >-
  Record model activation and shutdown duration per model per engine as switch
  cost
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T01:24:00Z'
labels: []
dependencies:
  - TASK-69
priority: high
ordinal: 71000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Switch cost stops being an assumption. Per model per engine, the router already knows when a switch started, when the target was first proved serving, and when the previous model stopped: those become a measured activation and shutdown duration rather than a log line. Where the numbers live, how long they stay valid, and exactly how the scheduler uses them are decided by the design row, not here; this row carries the deliverable and its tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 activation and shutdown durations are measured per model per engine from a switch's own timings, not read from config
- [ ] #2 the measured cost reaches the decision about which model to switch to next
- [ ] #3 a single sample or a stale cost cannot starve a model of its turn forever
- [ ] #4 SPEC 5.5 or 7 says what the numbers mean and what they never decide alone
<!-- AC:END -->
