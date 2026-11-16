---
id: TASK-2
title: Ticket-aware Pi session surface
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-16T23:22:00Z'
labels: []
dependencies: []
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Closes the AIR side of the Pi protocol in SPEC 13.3: replay of session events filtered by ticket, validation of a 'ready' ticket when the completion arrives, AIR model metadata on /v1/models, and a POST cancel alias.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Pi can replay only the events for its own ticket
- [ ] #2 A ready ticket is validated against the switch that produced it
- [ ] #3 Wire tests cover each addition and the OpenAI-compatible surface stays unchanged
<!-- AC:END -->

## Status notes

Landed in the agent-surface commit: `GET /admin/tickets/{id}/events` (ticket-scoped replay, pinned by
`AgentSurfaceTests`), `POST /admin/tickets/{id}/cancel`, and `X-Inference-Ticket` on every admitted
response — including error responses, so a caller can correlate a failure to the ticket it owns.

Still open, and why:

- #2 needs the switch identity the validation is checked against — that is TASK-10 plan step 3
  (immutable `(target, epoch, completion)` switch state). Validating against today's mutable
  per-target flag would pass the test and still mis-attribute a switch.
- `/v1/models` does not yet carry AIR model metadata (context window, slots, availability), and the
  Pi-side half (session extension) is a different repo entirely.
