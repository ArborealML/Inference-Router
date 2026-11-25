---
id: TASK-190
title: >-
  Survive an engine transition: drain before teardown, and never splice a
  continuation
status: Preparing
assignee:
  - '@chatswarm'
created_date: '2026-11-25T18:19:00Z'
labels: []
dependencies: []
priority: high
ordinal: 182000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the router tears an engine down while requests are in flight, the client connection simply ends. The owner's question is whether AIR should keep the connection open and resume underneath, or whether that belongs to the layer above that owns the transcript. Position so far: a true resume is impossible, because the engine's key-value cache and sampling state die with the process and tokens already sent cannot be un-sent. What is achievable is (a) do not tear down in-flight work in the first place, and (b) where nothing has been emitted yet, hold the connection open with keepalives and re-issue once against the new engine. Anything past that belongs to the caller, and AIR's obligation is to report the truth: a named reason, what was emitted, and the transition state. A design pass is dispatched to the external reasoning service on 2026-11-25.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A tier resize does not kill in-flight streams: it drains them, or refuses, and says which.
- [ ] #2 A stream with nothing emitted is retried once, within its deadline, against the successor engine, with the client connection held by keepalives.
- [ ] #3 A stream that already emitted tokens is never re-run: it ends in band with a named reason.
- [ ] #4 The named reason distinguishes a producer transition from a context limit from a stalled generation.
- [ ] #5 A client that stops reading and an engine that never returns are both bounded.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Related open items: reserve the switch's own ticket before a drain, drain the waiting queue by age-weighted value, keep an admitted stream alive to its first byte, bound the relay against a stalled generation. Do not change the pinned streaming byte shape.
<!-- SECTION:NOTES:END -->
