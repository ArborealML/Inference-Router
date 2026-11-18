---
id: decision-31
title: >-
  Serving gauges belong to the capability version, not to a live read
date: '2026-11-18T04:19:00Z'
status: accepted
updated_date: '2026-11-18T04:19:00Z'
---
## Context

Since v0.12.31 a switch claim reads one capability snapshot and compare-and-sets against the
version of that snapshot, which is what stopped a poll from buying a second launch of a model
another caller had already claimed. The serving gauges — context window, slots, inferred
profile — stayed outside that snapshot. `UpdateServingGauges` writes them from a probe result
without touching the version, and readiness answers the window question from the live gauge
while answering everything else from the snapshot. So a poll that widens the window between a
claim's readiness check and its CAS leaves the claim winning on a state that is no longer
true, and the engine stops and reloads a window it already serves.

## Decision

The gauges belong to the capability snapshot and to its version. A gauge change publishes
through the same fenced publish path that health and the model set use, and readiness answers
from one snapshot only. The fix removes live reads; it does not add a lock.

## Consequences

Gauge publications become version-bearing, so they must publish only on an actual change or
every health interval makes every claim race itself, and writes of an unknown value (0) stay
out of the version. A lost CAS now means re-decide, so the claim loop needs a bound on
re-decides per request and a named refusal when it runs out. Scheduler and escalation-ladder
reads of the gauges become snapshot reads, or stay live and go into SPEC §15 as accepted
gaps. Design and pins are TASK-68; the sequence that has to fail first is named there.

## Alternatives

Re-check the window after the CAS wins (rejected: two readings is exactly the failure this
removes). Guard the gauges with a lock held across the lifecycle transaction (rejected:
v0.12.32 removed that shape for a reason).
