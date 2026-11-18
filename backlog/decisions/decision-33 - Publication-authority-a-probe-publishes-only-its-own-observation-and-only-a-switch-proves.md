---
id: decision-33
title: >-
  Publication authority: a probe publishes only its own observation, and
  only a switch proves
date: '2026-11-18T05:41:14Z'
status: accepted
---
## Context

The TASK-68 plan review reported, and this seat re-checked in the code, that the ordinary health
round has two publishers with different authority and neither is fenced by the switch marker in the
same way. `PublishProbeResult` refuses to publish a positive while `SwitchingToward` is set;
`PublishProbeFailure` checks only the version it read, so a poll that starts after a switch has
published its proof and fails before the marker clears withdraws that proof and leaves a proven
engine unknown for a whole interval (`EngineRuntime.cs:235-247`). On the `wait_health = false` path
the switch itself published the proof before checking the window, so a switch that was about to be
refused had already told everyone the engine was serving (`:1013` against `:1038`). And the gauges
come from a third writer that does not fence at all.

## Decision

Publication authority is per publisher, not per field. An ordinary probe publishes only what it
observed, only at the capability version it read before the request went out, and not at all while a
switch marker is up — positive *and* negative. A switch is the only publisher allowed to make a
positive claim while the marker is up, and it publishes one atomic proof: the reported model set, the
health that came with it, and the serving gauges read after the launch. A switch's own state
observation stays local until that proof; a trust-path switch takes a fresh post-launch state read
rather than reusing a window read before it stopped the engine.

## Consequences

Suppressing negatives while the marker is up closes TASK-76 AC #2, and the same rule applied to the
gauges means no poll can widen or narrow a window a switch is deciding on. It also means a gauge
change that is real must be published by the switch that caused it, so a settle that reads a narrow
window after a launch cannot inherit the pre-switch wide one. The transition to unknown publishes
once — a state change, not a value change — and repeats of the same state are quiet; that refines
decision-31's wording about keeping 0 out of the version, which would otherwise let a state response
that stopped reporting a window leave the old wide window authoritative.

## Alternatives

Give every publisher its own generation counter (rejected: two validations per claim and a
double-collect in every reader). Let the switch re-read after its proof and repair what a poll did to
it (rejected: that is a third publisher racing the first two).
