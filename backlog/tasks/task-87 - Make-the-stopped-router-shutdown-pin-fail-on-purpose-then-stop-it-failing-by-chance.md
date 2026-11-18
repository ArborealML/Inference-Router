---
id: TASK-87
title: >-
  Make the stopped-router shutdown pin fail on purpose, then stop it failing by
  chance
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:27:00Z'
labels:
  - flake
  - tests
dependencies: []
priority: high
ordinal: 86000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Second flaky pin, separate from TASK-80's nudge case: SwitchClaimTests.cs:228 AWaiterInsideTheReadinessWaitIsToldTheRouterStopped times out at its 30 s Fact ceiling. Implementer's counts at 6ea83ca: 1 failure in the first 8 combined-filter runs after the two new pins, 0 in the 16 after; 0 in 10 such runs at f89fead; once in four runs of the combined switch filter; it also timed out once under a source mutation and passed alone. Load-dependent, never reproduced on demand — and a red main costs the next real failure its reading.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A construction fails the pin in 10 or more of 20 focused Release runs, with counts in this row.
- [ ] #2 The mechanism is named here and explains that reproduction, not a hypothesis.
- [ ] #3 Afterwards: 50 focused Release runs and 3 full Release suite runs with no failure.
- [ ] #4 The pin still fails when the shutdown hunk it protects is reverted.
<!-- AC:END -->
