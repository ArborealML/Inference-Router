---
id: TASK-80
title: >-
  Make the nudge-ordering test in CapabilityPublicationTests fail on purpose,
  then stop it failing by chance
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T03:46:00Z'
labels:
  - concurrency
dependencies: []
priority: high
ordinal: 80000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A named flake, with numbers and no reproduction anyone can fire on demand. The implementer measured 2 failures in 20 focused Release runs after their change, and 3 in 20 at 970c25b with only EngineRuntime.cs reverted; this seat measured 0 in 12 focused runs and 0 in 3 whole-class Release runs at a228c6e. The rate is real but load-dependent. It asserts `Assert.False(last.Healthy)` at CapabilityPublicationTests.cs:484, and the test never starts the health loop, so the stale-poll guess is excluded. Leading hypothesis, the implementer's and not yet confirmed: the sample asserted on is the **first** switch's nudge rather than the failed switch's. A flake with no reproduction eats CI runs and hides the next real failure, so the deliverable here is a construction that fails reliably — not a fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A construction fails 18 or more of 20 focused Release runs, with the counts recorded in this row
- [ ] #2 The mechanism is named in this row rather than guessed, and it explains the reproduction
- [ ] #3 Afterwards the pin is deterministic: 50 focused Release runs and 3 full Release suite runs, no failure
- [ ] #4 The pin still fails when the hunk it protects is reverted
<!-- AC:END -->
