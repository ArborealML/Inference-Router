---
id: TASK-190
title: >-
  Survive an engine transition: drain before teardown, and never splice a
  continuation
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:19:00Z'
updated_date: '2026-11-26T11:44:00Z'
labels: []
dependencies: []
priority: high
ordinal: 182000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the router tears an engine down with requests in flight, the client connection simply ends. The owner asked whether AIR should hold the connection open and resume underneath, or leave that to the layer above that owns the transcript. Position of record: a true resume is impossible, because the engine's key-value cache and sampling state die with the process and tokens already sent cannot be un-sent. What is achievable is not tearing in-flight work down at all and, where nothing has been emitted, holding the connection with keepalives and re-issuing once against the successor engine. Beyond that, AIR's obligation is the truth: a named reason, what was emitted, and the transition state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A tier resize does not kill in-flight streams: it drains them, or refuses, and says which.
- [ ] #2 A stream with nothing emitted is retried once, within its deadline, against the successor engine, with the client connection held by keepalives.
- [ ] #3 A stream that already emitted tokens is never re-run: it ends in band with a named reason.
- [ ] #4 The named reason distinguishes a producer transition from a context limit from a stalled generation.
- [ ] #5 A client that stops reading and an engine that never returns are both bounded.
- [ ] #6 One lifecycle owner fences admission atomically with reservation, so no dispatch races a teardown and an older generation's probe or completion cannot affect a new one.
- [ ] #7 A drain that cannot finish inside its bound reopens the still-valid engine and backs off without killing inference, and a controller restart reconciles container and generation state.
- [ ] #8 A container replaced out of band between two readiness probes cannot have its narrower published capacity suppress an owed relaunch, or the unprotected interval is documented in SPEC.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan written on main at db2d245 against the drain seams in RouterCore.Admission.cs, EngineRuntime.cs and HostReservation. A design pass on it is in flight with the external service. Wake: that pass landing. The unverified sibling-stop lead (around EngineRuntime.cs:1170-1175 and EngineLifecycleCoordinator.cs:154-161) stays a lead: no line there was read, and if a generic switch really can stop owned peers without cancelling their streams, the fix belongs in this drain contract, not in the ticket-end reporting task.
<!-- SECTION:NOTES:END -->
