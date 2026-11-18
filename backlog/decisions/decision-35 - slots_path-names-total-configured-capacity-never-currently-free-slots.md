---
id: decision-35
title: >-
  `slots_path` names total configured capacity, never currently free slots
date: '2026-11-18T05:41:14Z'
status: accepted
---
## Context

The serving gauge read from `health_probe.slots_path` (default `total_slots`) feeds three things:
admission (`min(scheduler.max_inflight_per_engine, reported slots)`, `EngineRuntime.cs:1128`), the
per-profile ladder's `max_slots` comparison in §7.6, and — once decision-31 puts the gauges into the
capability version — the capability version itself. The shipped wording calls it "active concurrent
slots" (`SPEC.md:1613`), which reads either as configured capacity or as how many requests are in
flight right now. Those are different signals, and the second one is read by the scheduler on every
dispatch.

## Decision

`slots_path` names the engine's **total configured slot capacity** for the window it is serving, not
a count of currently in-flight requests. A free-slot or in-flight counter is rejected as the meaning
of this gauge: it would bump the capability version on every request, so every switch claim would
race a number that changes by design, and every capability scan would read a different instant.
Because slots do not answer the readiness question (`ReadyIn` reads health, the reported model set and
the window), a slot-only change may conservatively retire a switch claim; that is accepted rather
than tuned away.

## Consequences

SPEC §7.6 and the §5.3.1 gauge wording have to say capacity, with the fallback to
`max_inflight_per_engine` unchanged. Slots stay an **admission** gauge and an observability field: a
switch's settle proof validates health, the reported model name and the window, not the exact slot
count, so an engine that launches with a different slot count than the profile declares is not
refused — the discrepancy is recorded in SPEC §15 rather than fixed by a new rule. Inferring the
serving profile name from a window must not infer a slot count with it, and an inferred name is
observability only: it never authorizes readiness.

## Alternatives

Read the gauge as free slots and use it for back-pressure (rejected: version churn per request, and
back-pressure belongs to the queue caps in §5.4). Split capacity and occupancy into two gauges (not
now: nothing in the current scheduler acts on occupancy, and a second version-bearing gauge inherits
the same churn question with no consumer).
