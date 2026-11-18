---
id: decision-41
title: >-
  Capability internals stay internal: the admin surface is EngineStats, and no
  criterion authorizes widening it
date: '2026-11-18T06:35:00Z'
status: accepted
---

## Context

The provenance work on the resume read adds a member to the engine's capability record, and the
frozen wire contract makes any new field on a published body a wire break. Checked at the
implementer's report and re-checked here at `6ea83ca`: `EngineCapability`
(`src/Arboreal.InferenceRouter/Scheduling/CapabilityScan.cs:19-24`) is named nowhere in the Server
project, and every capability-shaped answer is a separate DTO copying fields out of one snapshot —
`EngineRuntimeInfo` (`RouterCore.cs:455-470`), which feeds the scheduler, and `EngineStats`
(`RouterCore.cs:1039-1055`), the record `/admin` publishes (`Healthy`, `LoadedModels`,
`SwitchingToward`, `ActiveProfile`, `ActiveContextWindow`, `ActiveSlots`). What the operator needs
already reaches them as events: `engine.health` carrying `value: 0` with a reason when the model
fetch failed (`EngineRuntime.cs:390`) against `engine.health` with the model count on success
(`:371`), and `engine.switch_started` for a claim (`:935`).

## Decision

Engine internals stay internal. A provenance member belongs to the capability record and is not
mirrored into `EngineStats` or any other published record: adding a member to a `/admin` body is a
wire change under SPEC 6 and no acceptance criterion asks for it. If the operator surface should
name the author of a health value, that is a separate SPEC 6 decision with its own row, not a side
effect of an internal fix.

## Consequences

The provenance fix stays contained: one record in the library and the publication sites in
`EngineRuntime`, no wire surface. The cost is diagnostic — an operator reading `/admin` cannot tell
a probe-withdrawn unknown from a claim-withdrawn unknown and reads the feed instead. Pins keep
asserting on feed events and outcomes rather than on the new member, so an internal field never
becomes the only evidence that a behaviour holds.
