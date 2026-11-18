---
id: TASK-98
title: >-
  Make one stop decision visible to an in-flight claim, so a switch cannot
  authorize after the router decided to stop
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:02:00Z'
labels: []
dependencies: []
ordinal: 97000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The top-tier plan review for the flaky shutdown pin ([TASK-87](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-87%20-%20Make-the-stopped-router-shutdown-pin-fail-on-purpose-then-stop-it-failing-by-chance.md)) split the work: that row proves the *switch-cancellation* contract, and this row carries the *stop-decision* half. Today `SetStopCauseLocked` overwrites the cause and can re-arm the cancellation token source per generation, so a claim that read the switch decision before the stop keeps running against it, and a waiter can be released with one cause and reclassified by another. One stop decision, frozen, monotonic, and visible to an in-flight claim, makes both readings agree.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 The stop cause is written once and never rewritten, and the cancellation token source is not re-armed after a stop
- [ ] #2 A claim that passed the switch gate before the stop sees the stop: it is released with a cause, and the cause it reports cannot change afterwards
- [ ] #3 A named test fails when the stop cause is re-armed or overwritten, and a second named test fails when the claim path ignores it
- [ ] #4 SPEC says which cause wins when a stop and a switch race, in §5.5, in the same commit as the code
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by the plan review dispatched for TASK-87 (top reasoning tier): the review withdrew the plan's proposed mechanism and named this production half instead. Evidence for the current behaviour is in that row's Notes. This row is the one that can fix the second flake shape — a waiter released as `cancelled` and then reclassified.
<!-- SECTION:NOTES:END -->
