---
id: TASK-71
title: >-
  Record model activation and shutdown duration per model per engine as switch
  cost
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T04:10:00Z'
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
- [ ] #1 Activation and shutdown durations are measured per model per engine from a switch's own timings, not read from config
- [ ] #2 The same arrivals pick a different model once the measured cost changes, pinned, so the number is an input to the choice and not a report
- [ ] #3 A single sample or a stale cost cannot starve a model of its turn forever
- [ ] #4 SPEC 5.5 or 7 says what the numbers mean and what they never decide alone
- [ ] #5 5 Each stage is recorded as its own value, an unobserved stage is distinguishable from a zero, and a failed or cancelled attempt is still recorded with its outcome
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A goal-level audit of the switching rows found the old criterion #2 passable with the measured cost having no influence at all: a number that reaches the decision and changes nothing satisfies it. #2 now requires the choice to move when the cost moves.

The external evaluation of an open-source gateway (2026-11-18) argues this row must not wait for the adaptive design: stage timings need no database and no change to `/v1`. It also names the shape to copy — record each stage separately (queue, stop, load, confirmed readiness, first forwarding, terminal outcome), separate a missing or failed observation from a zero, and classify attempts as successful, failed, cancelled or overtaken. Estimates stay advisory: they never license admission and never relax confirmation.
<!-- SECTION:NOTES:END -->
