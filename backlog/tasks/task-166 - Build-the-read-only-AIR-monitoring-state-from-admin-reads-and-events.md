---
id: TASK-166
title: Build the read-only AIR monitoring state from admin reads and events
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:09:00Z'
updated_date: '2026-11-22T04:48:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-165
references:
  - SPEC.md
ordinal: 161000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Read status periodically, follow the global event stream and fetch known tickets. Keep this C# state independent of terminal widgets for reuse by Blazor.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Current snapshots supply health, loaded model, switch target, context, slots, in-flight counts and queue depths across configured engines.
- [x] #2 A known ticket can be followed by ID using existing read-only endpoints. A fresh snapshot corrects outdated gauges.
- [x] #3 Engine capacity and queue depth use current router snapshots. Ticket history is bounded to observed events and tracked by exact ticket ID.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse public admin DTOs or explicit source-generated read models. Bound retained ticket data. Integrate unfiltered event sequences and status snapshots without double counting. Keep UI dependencies out of state.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The read-only monitor loads authoritative engine and queue snapshots, looks up an exact followed ticket, and reads bounded global event history. Review of 5e4579f identified mutable gauge storage. The correction at 3339404 publishes immutable gauges and passed the focused tests. [Implementation and verification](https://github.com/ArborealML/Inference-Router/pull/79).
<!-- SECTION:NOTES:END -->
