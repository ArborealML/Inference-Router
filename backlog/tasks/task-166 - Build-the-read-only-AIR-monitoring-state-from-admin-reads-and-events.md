---
id: TASK-166
title: Build the read-only AIR monitoring state from admin reads and events
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:09:00Z'
updated_date: '2026-11-22T04:29:00Z'
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
- [ ] #1 Current snapshots supply health, loaded model, switch target, context, slots, in-flight counts and queue depths across configured engines.
- [ ] #2 A known ticket can be followed by ID using existing read-only endpoints. A fresh snapshot corrects outdated gauges.
- [ ] #3 Engine capacity and queue depth use current router snapshots. Ticket history is bounded to observed events and tracked by exact ticket ID.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse public admin DTOs or explicit source-generated read models. Bound retained ticket data. Integrate unfiltered event sequences and status snapshots without double counting. Keep UI dependencies out of state.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Shared read-only client at 5e4579f passed both focused tests covering authoritative gauges, bounded history, JSON feed cursor and exact ticket lookup. Independent review dispatched.
<!-- SECTION:NOTES:END -->
