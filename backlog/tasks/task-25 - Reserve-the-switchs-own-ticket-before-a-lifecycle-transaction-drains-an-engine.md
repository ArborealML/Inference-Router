---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-17T02:41:00Z'
updated_date: '2026-11-19T19:49:00Z'
labels:
  - engine
  - lifecycle
milestone: m-4
dependencies: []
priority: high
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent plan verdict will wake Scheduling. The November 19 plan review covers the queued native switch owner and logical accounting on b25c259. Production sources still match that base after PR #35. The reviewer is checking whether a provisional reservation changes a reachable decision before host enrollment and which existing controls can cover it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A switch request is distinguishable from ordinary inflight work and is excluded from the drain count
- [ ] #2 Admission to the engine is closed for the duration of the transaction, and a request aimed at it is queued rather than admitted
- [ ] #3 Existing inflight requests finish before anything is stopped, and the drain has a timeout with an actionable outcome
- [ ] #4 Queue counters and terminal events still agree with what actually happened to each ticket
- [ ] #5 Pause a waiter after Ready and begin a competing switch. Its request reaches only the instance its reservation authorizes, or returns to admission before any upstream write.
- [ ] #6 Hold an upstream send past caller timeout. Verify one caller terminal, a released logical slot, and zero stop or rebind operations through drain expiry. After late-response disposal, a fresh authorized drain can proceed.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Propose carrying the selected ticket and its single existing logical charge into readiness. Attach the owner only when that ticket installs the switch claim. Preserve direct starters, independent joiners, cancellation, terminal delivery and eventual release. Reuse the held fake-native provider for exact send and cancellation controls. Enrolled closure, authoritative physical drain, protected Ready-to-send promotion and ambiguous effects remain open.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SPEC 15 names this row, TASK-64 AC #3 and TASK-72 as the owners of the window the v0.12.35 resume read leaves open: a Ready answer is not protected admission, because the successor whose switch was already gated may unload the model before the waiter's request reaches the engine.

**Proof check the deep review supplied for AC #5:** hold a waiter between its Ready answer and actual receipt at the engine, let the successor's unload complete, and show that dispatch is held or redirected by a reservation or drain rule rather than sent to the displaced model. A test that only orders the two switches does not pin this.
<!-- SECTION:NOTES:END -->
