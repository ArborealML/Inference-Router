---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-17T02:41:00Z'
updated_date: '2026-11-19T19:59:00Z'
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
A production enrollment path that can close admission and drain the incumbent before stop will wake this task. Defer the owner-only change. Review on b25c259 confirmed that selection already charges the ticket once and only the successful installer owns the switch. Attaching an identity alone changes no drain, stop or first-send outcome.
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
Deliver the reservation with its first production consumer. Preserve the selected ticket’s single logical charge and keep direct/admin switches ownerless. Before native stop, close admission and exclude the triggering provisional owner from the drain while accounting for outstanding physical inference uses. Local HTTP uses include health traffic and cannot prove backend retirement. Coordinate the interface with host enrollment, avoiding a circular prerequisite. Reuse existing held-provider and cancellation evidence when qualifying the combined behavior.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SPEC 15 names this row, TASK-64 AC #3 and TASK-72 as the owners of the window the v0.12.35 resume read leaves open: a Ready answer is not protected admission, because the successor whose switch was already gated may unload the model before the waiter's request reaches the engine.

**Proof check the deep review supplied for AC #5:** hold a waiter between its Ready answer and actual receipt at the engine, let the successor's unload complete, and show that dispatch is held or redirected by a reservation or drain rule rather than sent to the displaced model. A test that only orders the two switches does not pin this.
<!-- SECTION:NOTES:END -->
