---
id: TASK-190
title: >-
  Survive an engine transition: drain before teardown, and never splice a
  continuation
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:19:00Z'
updated_date: '2026-11-26T01:07:00Z'
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
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design pass landed 2026-11-25 and its follow-up review 2026-11-26, both external pro tier; documents held outside the repository. Accepted findings: fence admission atomically with reservation rather than as a separate guard, so no dispatch races a teardown; a drain that cannot finish inside its bound reopens the still-valid engine with a persisted backoff instead of killing inference; after a stop, recovery and container-generation reconciliation are bounded; a queued request with a short deadline cannot survive a 110 s sole-resident restart, so it is rerouted or the resize is declined; queues are bounded and keep age, deadline and priority aging, and partial streams are never replayed. Next action: the implementation plan, after the containment branch lands.
<!-- SECTION:NOTES:END -->
