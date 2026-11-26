---
id: TASK-196
title: Record the demand, target and resident state behind a switch that does start
status: To Do
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-26T09:10:00Z'
labels:
  - events
  - scheduling
dependencies:
  - TASK-189
priority: high
ordinal: 188000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The containment work proved that a satisfied demand starts no switch, but a switch that does start still lands in the feed as a bare target change with no explanation of the demand that required it, the capacity the resident measured at the moment of decision, or the lanes and window the target declares. An operator reading the feed after an outage cannot tell a necessary switch from a wasted one. This task makes the switch decision auditable: the event names the resolved demand, the resident's measured window and lanes, the chosen target and its declared pair, and the reason the resident did not satisfy the demand.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A switch event records the resolved demand, the resident's measured window and lane count at decision time, the chosen target's declared window and lane count, and a named reason the resident did not satisfy the demand.
- [ ] #2 One test produces a switch from a demand the resident cannot satisfy and asserts those fields, and one asserts that a satisfied demand emits no such event.
- [ ] #3 The event stays additive: no existing event field, code or sequence changes, and a client that ignores the new fields sees the same stream as before.
<!-- AC:END -->
