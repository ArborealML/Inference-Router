---
id: TASK-201
title: Hold a client through an engine transition instead of ending its connection
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T12:20:00Z'
labels:
  - wire
  - scheduling
dependencies:
  - TASK-190
priority: high
ordinal: 193000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the router tears an engine down with requests in flight, the client connection simply ends. A true resume is impossible: the engine's key-value cache and sampling state die with the process and sent tokens cannot be un-sent. What is achievable is not tearing in-flight work down at all, and where nothing has been emitted, holding the connection with keepalives and re-issuing once against the successor engine. Beyond that the router owes the truth: a named reason, what was emitted, and the transition state. The lifecycle mechanics — drain ownership and the durable disposition — live on the dependency; this task is the client-facing contract.
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
Split out of the drain task by the word budget: two deliverables, one client contract and one lifecycle disposition. Overlaps the stalled-generation and slow-consumer bounds on task-30 and the end-reason naming on task-187: keep one reason vocabulary rather than inventing a second. Wake condition is the drain task's contract landing.
<!-- SECTION:NOTES:END -->
