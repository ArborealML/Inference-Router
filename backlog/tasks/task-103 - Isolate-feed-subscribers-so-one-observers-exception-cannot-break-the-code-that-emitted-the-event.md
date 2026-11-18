---
id: TASK-103
title: >-
  Isolate feed subscribers so one observer's exception cannot break the code
  that emitted the event
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:42:00Z'
labels: []
dependencies: []
ordinal: 102000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`RouterEvent.cs:85` invokes every feed subscriber inside the append, in the emitter's own thread, with no isolation: one subscriber that throws propagates into whatever produced the event. Verified by this seat — the loop is a bare `foreach` over a snapshot, with no `try`. Today every producer that emits near a critical section (a switch marker, a claim, a terminal settle) is one bad observer away from leaving that section half-done. The relay and refusal work both add emits near such sections, so this is the moment to fix it once.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 A subscriber that throws cannot break the code that emitted the event, and the failure is visible somewhere an operator can see it
- [ ] #2 A pin emits to a subscriber that throws and asserts the producer's state completes and the other subscribers still receive the event
- [ ] #3 SPEC §6.1 says what a slow or faulting subscriber costs the feed — dropped, isolated, or unsubscribed — in one sentence
- [ ] #4 The emit runs outside the switch lock, and a subscriber that throws leaves the caller's answer exactly as it was
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found while dispositioning the TASK-94 plan review, then verified in the tree at `RouterEvent.cs:85`. Kept out of TASK-94 because TASK-94 only needs the guard at its own emit site; this row is the general rule for every producer, including the admin SSE layer that already drops stale subscribers silently nearby.
<!-- SECTION:NOTES:END -->
