---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:41:00Z'
labels:
  - engine
  - lifecycle
dependencies: []
priority: high
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The lifecycle coordinator now runs ownership proof, preflight, stop and launch in one order, but two steps in the approved transaction are missing on purpose: closing admission to the engine, and draining the requests already running on it. Neither can be added where they sit today. A switch is started by an admitted request that counts as inflight and then waits for the switch to finish, so a drain-to-zero would wait on the request that asked for the switch. The scheduler has to hold the switch ticket aside first, and that touches queue accounting and the terminal-event semantics the parity work is already carrying.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A switch request is distinguishable from ordinary inflight work and is excluded from the drain count
- [ ] #2 Admission to the engine is closed for the duration of the transaction, and a request aimed at it is queued rather than admitted
- [ ] #3 Existing inflight requests finish before anything is stopped, and the drain has a timeout with an actionable outcome
- [ ] #4 Queue counters and terminal events still agree with what actually happened to each ticket
<!-- AC:END -->
