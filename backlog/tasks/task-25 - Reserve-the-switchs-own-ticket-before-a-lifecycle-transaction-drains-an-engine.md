---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:41:00Z'
updated_date: '2026-11-18T06:27:00Z'
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
- [ ] #5 A request authorised at admission cannot lose that authority between the wait returning Ready and the engine receiving it; any gap left open is named in SPEC with its window
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SPEC 15 names this row, TASK-64 AC #3 and TASK-72 as the owners of the window the v0.12.35 resume read leaves open: a Ready answer is not protected admission, because the successor whose switch was already gated may unload the model before the waiter's request reaches the engine.

**Proof check the deep review supplied for AC #5:** hold a waiter between its Ready answer and actual receipt at the engine, let the successor's unload complete, and show that dispatch is held or redirected by a reservation or drain rule rather than sent to the displaced model. A test that only orders the two switches does not pin this.
<!-- SECTION:NOTES:END -->
