---
id: TASK-25
title: >-
  Reserve the switch's own ticket before a lifecycle transaction drains an
  engine
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-17T02:41:00Z'
updated_date: '2026-11-25T23:57:00Z'
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
Plan review of the containment branch (external, 2026-11-25) places the safety-critical half of the busy-resize problem here, not in the no-op guard: reserve the switch's own ticket, mark the engine draining atomically under the same lock as admission, fence new admissions, wait for leases that cover first-byte generation and downstream relay, recheck zero leases and resident state in the same synchronisation as the stop, fail closed with queued tickets expired with a reason when a reader is stuck, and never timer-expire a still-live lease and then stop. Deduplicate deferral events; include the two-minute reload and lost cache in switch cost; cost must never override safety.
<!-- SECTION:NOTES:END -->
