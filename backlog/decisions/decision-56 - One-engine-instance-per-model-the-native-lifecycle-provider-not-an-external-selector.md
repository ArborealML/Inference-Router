---
id: decision-56
title: >-
  One engine instance per model; the native lifecycle provider, not an external
  selector
date: '2026-11-18T17:45:00Z'
status: accepted
---

## Context

The owner called a design drift at 17:31Z — one engine instance should be associated with one model, and
swapping engines is required, with intra-engine model swapping deferred — then corrected the framing at
17:41Z: the router is **not** to exec an external selection program; that program's usage pattern is the
behavioural reference only. Both routes are in this tree today: `samples/router.toml:102,113` wire a `select`
exec, while TASK-1 landed the native provider and SPEC §5.3.1 states the config shape (`lifecycle = "docker"`
plus `[engine.<name>.docker]`, and a model carrying both a `select` command and native lifecycle is a load-time
error — read at `2b751ec`, SPEC `:1406`, `:1431`). No shipped sample carries a lifecycle block, and
`EngineRuntime.cs:709` refuses a switch for any engine marked `remote = true` with `no_lifecycle_owner`.

## Decision

The unit of a switch is one engine instance per model: stop the instance holding one model, start the instance
for the next, in the reference order — bring up, health-gate, serve; never stop an unrelated process; per-model
profiles; active requests ended before the stop. The native lifecycle provider is the route; an external
selector program is not a runtime dependency, and its pattern is documentation. Intra-engine model swap stays
later work. Reloading configuration without a restart becomes a requirement (TASK-107), because a swap route
that needs a restart costs the queue and cannot be exercised.

## Consequences

TASK-77's reach/ownership split stops being a design nicety: an engine the router can talk to but does not own
has no switch path at all, and that is the posture every remote sample and the bring-up config sit in. TASK-25
(reserve the switch's own ticket before a drain) and TASK-26 (§5.3.1 provider gaps, now including the sample's
lifecycle posture) are the capability work; TASK-33 documents the fakes-only verification posture. The owner parked live engine lifecycle on this host at 17:55Z. TASK-112 owns the later authorized swap proof. `select = "exec"` stays supported as the default route, but is legacy: SPEC and the samples
must say so rather than leave a sample pointing at an external script. decision-50's read-only-after-boot
premise is bounded by TASK-107.
