---
id: decision-82
title: Set the reasoning level per implementer model, not per seat
date: '2026-11-26T02:03:00Z'
status: accepted
---
## Context

The contract names one reasoning level for implementer sessions because it assumed one implementer model.
Implementation now runs on more than one model, and a level chosen for one is wrong for another: the same
containment task lost three implementer runs to infrastructure and produced a shared-predicate widening
that a deeper pass should have caught before it reached review. A coordinator under pressure inherits its
own reduced thinking level into the worker it dispatches, which is how a lowered seat quietly lowers the
whole line.

## Decision

Reasoning level follows the model the implementer runs on, set at dispatch and not inherited from the
dispatching seat. A GPT-6.2 Sol implementer runs at high. A Qwen 3.8 Flash Next implementer runs at
extra-high; the default high is too shallow for scheduling, concurrency and wire-contract work on that
model. Plan and commit reviews keep their own tier rule: the deep tier for ordinary work, the top tier for
complex work and anything touching the wire contract or a live machine.

## Consequences

Every dispatch states the level explicitly instead of relying on a harness default. A worker resumed or
restarted after a crash is dispatched at its model's level, not at the level of the seat that lost the
previous run. The coordinator keeps its own reduced level for board work without dragging implementation
down with it.
