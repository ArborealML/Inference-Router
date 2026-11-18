---
id: decision-40
title: 'A state flag is not evidence of what set it: an inference must name its author'
date: '2026-11-18T06:19:00Z'
status: accepted
---

## Context

The deep review of the v0.12.34/v0.12.35 pair found, and this seat re-checked in code at `f89fead`,
that `OwnProofStillStands` reads "health absent and a switch marker up" as though the marker had
withdrawn the health. `PublishProbeFailure` can withdraw health first — it is not fenced by the
marker, that fence being another row's criterion — and the switch-start exchange then rewrites
`Healthy = false` in its own publication. The snapshot carries one bit, so the read site cannot tell
a probe's observation from a claim's transaction, and can answer Ready on health that nobody has
confirmed since a failure. Same shape as decision-38: a proxy standing where a measurement belongs.

## Decision

A state flag is not evidence about which publisher produced that state. Any inference of the form
"this absence must have been caused by X" has to read authorship, not value: a publisher names
itself in what it publishes, and a reader decides from that provenance (or from who holds authority
until a named point). Where authorship is not available the inference is refused rather than assumed
— decision-27's posture applied inside the engine, not only at the wire.

## Consequences

Publication sites carry a cost: every publisher identifies itself, and a reader that reasons about an
absence takes one snapshot including that provenance. The resume read is pinned by TASK-61 AC #7, the
probe side is fenced by TASK-76, and TASK-68 puts proof, health and gauges in one snapshot so the
three are never read from different places (decision-31).
