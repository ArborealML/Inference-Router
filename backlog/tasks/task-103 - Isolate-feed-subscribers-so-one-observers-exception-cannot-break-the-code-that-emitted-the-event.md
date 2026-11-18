---
id: TASK-103
title: >-
  Isolate feed subscribers so one observer's exception cannot break the code
  that emitted the event
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:42:00Z'
updated_date: '2026-11-18T17:41:00Z'
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
- [x] #1 A subscriber that throws cannot break the code that emitted the event, and the failure is visible somewhere an operator can see it
- [x] #2 A pin emits to a subscriber that throws and asserts the producer's state completes and the other subscribers still receive the event
- [x] #3 SPEC §6.1 says what a slow or faulting subscriber costs the feed — dropped, isolated, or unsubscribed — in one sentence
- [x] #4 The emit runs outside the switch lock, and a subscriber that throws leaves the caller's answer exactly as it was
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found while dispositioning the TASK-94 plan review, then verified in the tree at `RouterEvent.cs:85`. Kept out of TASK-94 because TASK-94 only needs the guard at its own emit site; this row is the general rule for every producer, including the admin SSE layer that already drops stale subscribers silently nearby.

Half of #4 landed at `51922fe`, read there: the switch-refusal `engine.switch_failed` is emitted after the switch lock is released, so no subscriber runs holding an engine lock.

Closed at `2b751ec`, read on the pushed tree: `EventRing.Append` invokes each subscriber in its own `try` and counts the fault (`RouterEvent.cs:97-107`, `SubscriberFaults` at `:118`), `/admin/status` publishes it as `FeedSubscriberFaults` (`RouterCore.cs:244`, DTO `:1098`), and SPEC §6.1 states both costs (`:1938-1941`): a faulting subscriber loses its own event and nothing else; a subscriber that *blocks* blocks every producer, because the append holds the ring lock — an observer that must not slow requests replays by seq. Four pins by name in `SubscriberFaultTests`, including the two that pin the producer's answer and the un-stranded waiter.

The implementer's control (their report): catch reduced to count-and-rethrow gives 4 failed / 0 passed across those pins, tree restored byte-identical. SPEC records the two controls unusable here — the pre-fix tree does not compile the pins, and dropping the catch is a CS0649 build failure, not a red pin. Their counts: 19/19 focused, 235/235 full (Debug), 0 warnings, load 14.4 self-induced. This seat's read: CI run 44360122393 success at `2b751ec`.
<!-- SECTION:NOTES:END -->
