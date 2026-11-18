---
id: decision-57
title: >-
  A feed observer's failure stays observer-local: the ring counts it, the
  observer owns it, and it never enters replay
date: '2026-11-18T18:00:00Z'
status: accepted
---

## Context

An outside review of `2b751ec` asked whether subscriber faults should carry per-subscription identity, be
recorded under the ring lock, or feed an ejection policy — and separately whether a permanently failing observer
should be dropped. Both questions come from reading the ring's per-subscriber `try` as the place where a feed
consumer's failure is handled. It is not: production has exactly two subscribers, the dispatcher's wake nudge
(`RouterCore.cs:71`) and the admin feed hand-off (`RouterCore.cs:305-316`), and the second one copies events into
a bounded channel with `TryWrite`, never performs I/O inside the ring, and closes its own stream when the buffer
fills. So the only in-tree consumer already owns its failure, and it does so outside the ring lock.

## Decision

The ring's catch is a **last-resort boundary**, not the feed's error-handling layer. A fault is counted once at
the ring (`SubscriberFaults`, additive on `/admin/status`) and is **never** written into the canonical ring: fault
records are observer-local and nondeterministic, so publishing them would perturb sequence numbers and replay
order for every reader. Anything that performs I/O on behalf of a subscriber — a future socket writer — catches
its own failure, unsubscribes, and cleans up outside the ring lock; the ring catch exists only so such a failure
cannot strand the producer mid-transition.

## Consequences

Richer diagnosis (subscriber identity, event kind and seq, consecutive-versus-total counts) belongs in admin
diagnostics or a separate bounded fault feed, never in replay, and is not built until a subscriber exists whose
failure an operator has to tell apart. The global counter is accepted as sufficient for the two observers this
tree has; if an I/O observer ships, its ejection rule ships with it in the same commit. TASK-109 carries the
evidence half: the pin at a terminal emit and the two additional controls.