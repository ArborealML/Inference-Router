---
id: decision-34
title: >-
  A claimed switch is completed not rolled back; a waiter answers from the
  current snapshot
date: '2026-11-18T05:41:14Z'
status: accepted
---
## Context

Putting the serving gauges into the capability version (decision-31) makes the switch claim's
compare-and-set lose more often: a committed gauge change between the readiness read and the marker
write now retires the claim. The claim loop that does that re-deciding runs while the caller holds
both the switch lock and the transition gate (`EngineRuntime.cs:640-676`), and a waiter whose own
switch reported success answers from the switch's boolean even when the current snapshot no longer
fits (`:782` against `:789`). Both halves were re-checked against the code at `3dce284`.

## Decision

A switch that has raised its marker is completed, not rolled back: past that point the router may
already have stopped or relaunched the engine, so a newer gauge observation is not rollback
authority. A caller that loses the claim releases both locks before re-deciding, so it cannot hold
the gate against the publisher that beat it, and its re-decides are bounded by a named count plus the
existing switch-wait deadline; exhaustion is an internal reason that maps to the existing
`switch_failed` code and 503, with no new switch event, because no switch was claimed. A waiter that
wakes answers from the current snapshot; the switch's own readiness boolean records that a
transaction proved something, it does not license a dispatch on its own.

## Consequences

Re-deciding becomes the normal outcome of a lost claim rather than a retry inside the gate, so the
bound needs a name and a reason of its own, and a re-decide that finds the model already proved takes
that proof rather than launching again. Because a waiter re-reads, a switch that succeeded and was
then withdrawn by a poll costs one more decision rather than a request sent to an engine that cannot
serve it. There is no ping-pong: readiness is `window >= target`, the target is the smallest fitting
profile, a wider window satisfies a smaller request, and nothing de-escalates automatically.

## Alternatives

Cancel and undo a claimed switch when a newer observation contradicts it (rejected: the lifecycle
mutation is not reversible). Let a successful switch keep answering Ready from its own flag
(rejected: that is the stale-window authorization defect re-opened at the waiter).
