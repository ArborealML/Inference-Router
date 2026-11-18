---
id: TASK-103
title: >-
  Isolate feed subscribers so one observer's exception cannot break the code
  that emitted the event
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:42:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-11
dependencies: []
ordinal: 102000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`RouterEvent.cs:85` invokes every feed subscriber inside the append, in the emitter's own thread, with no isolation: one subscriber that throws propagates into whatever produced the event. Verified by this seat — the loop is a bare `foreach` over a snapshot, with no `try`. Today every producer that emits near a critical section (a switch marker, a claim, a terminal settle) is one bad observer away from leaving that section half-done. The relay and refusal work both add emits near such sections, so this is the moment to fix it once.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A subscriber that throws cannot break the code that emitted the event, and the failure is visible somewhere an operator can see it
- [x] #2 A pin emits to a subscriber that throws and asserts the producer's state completes and the other subscribers still receive the event
- [x] #3 SPEC §6.1 says what a slow or faulting subscriber costs the feed — dropped, isolated, or unsubscribed — in one sentence
- [x] #4 The emit runs outside the switch lock, and a subscriber that throws leaves the caller's answer exactly as it was
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found while dispositioning the TASK-94 plan review, verified in the tree at `RouterEvent.cs:85`. Kept out of TASK-94, which needs the guard only at its own emit site; this row is the general rule for every producer.

#4's first half landed at `51922fe`: the switch-refusal `engine.switch_failed` is emitted after the switch lock is released, so no subscriber runs holding an engine lock. The second half — a throwing observer leaves the caller's answer exactly as it was — is pinned at `2b751ec`.

Review of `2b751ec` (top tier, ingested 17:58Z): the charges that a subscriber may have written a partial SSE frame, that one global count is too coarse to diagnose, and that a failing subscriber should be ejected all assume the ring subscriber is the network writer. It is not; the two production subscribers and their reads are in TASK-109 Notes. The residue — pin separation, the ordering pin, and two further controls — is on TASK-109, not here.
<!-- SECTION:NOTES:END -->
