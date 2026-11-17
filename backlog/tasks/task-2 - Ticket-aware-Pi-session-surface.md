---
id: TASK-2
title: Ticket-aware Pi session surface
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T00:35:00Z'
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
- [ ] #1 Pi can replay only the events for its own ticket
- [ ] #2 A ready ticket is validated against the switch that produced it
- [ ] #3 Wire tests cover each addition and the OpenAI-compatible surface stays unchanged
<!-- AC:END -->

## Status notes

**Correction:** the note here previously claimed `GET /admin/tickets/{id}/events`,
`POST /admin/tickets/{id}/cancel` and a ticket-scoped replay test had landed. They had not: the surface
is `GET /admin/tickets/{id}` (read) and `DELETE /admin/tickets/{id}` (cancel), `RouterEventFilter` has
no ticket field, and `AgentSurfaceTests` pins the tool manifest and `X-Inference-Ticket`, not replay.
AC #1 is unchecked again. What is actually shipped: `X-Inference-Ticket` on every admitted response
(including handled failures), so a caller can reach the two ticket routes without scraping the stream.

To close AC #1: add `TicketId` to `RouterEventFilter` and `Matches`, expose ticket-scoped replay
(`GET /admin/tickets/{id}/events`, or `?ticket=` on `/admin/events`), and pin it with a wire test that
asserts another ticket's events are absent. The `POST .../cancel` alias is listed in SPEC §13.3 as
still needed, not as shipped.

Still open, and why:

- #2 needs the switch identity the validation is checked against — that is TASK-10 plan step 3
  (immutable `(target, epoch, completion)` switch state). Validating against today's mutable
  per-target flag would pass the test and still mis-attribute a switch.
- `/v1/models` does not yet carry AIR model metadata (context window, slots, availability), and the
  Pi-side half (session extension) is a different repo entirely.
