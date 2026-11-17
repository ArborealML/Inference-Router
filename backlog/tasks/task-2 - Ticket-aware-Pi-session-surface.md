---
id: TASK-2
title: Ticket-aware Pi session surface
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-17T05:27:00Z'
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Fix slice (event-feed correctness first, ticket failure paths second). Each item lands with a test.

1. Replay cursor semantics are exclusive, matching the filter that reads them. A cursor ahead of the ring (a client reconnecting with a seq from a previous router lifetime) yields nothing instead of computing a negative count and throwing after the SSE headers have already been flushed.
2. Replay default cap = ring capacity, so a reconnect inside the retained window is never silently truncated; `OldestSeq` names the retention boundary, which becomes the only place events disappear.
3. Replay snapshot and live subscription taken under one ring lock, so the handover window can neither lose an appended event nor deliver it twice.
4. A subscriber that falls behind has its feed closed rather than silently dropping: a filtered reader sees no seq gap, so a dropped terminal event would otherwise look like a stream with nothing more coming. Live subscriber count is bounded, and a feed ends when the router shuts down instead of outliving it.
5. Next half of the slice: every terminal failure path completes the admitted stream channel, so a caller is never left waiting on a stream that will not end; caller cancellation stays a cancellation rather than being counted as a completed request.

Out of this slice, deliberately: counting per-subscriber drops and reporting the loss on the wire. That is a wire extension and it belongs with the event-schema work, so it is cross-linked to the hardening task rather than bundled here.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
An external review of the three board-only commits that recorded these findings is in flight; it re-checks the findings against the code and the priority order of the fix slice. An in-flight review does not change this task status. Nothing from it is adopted until verified against this code.

A second external pass over the fix slice found two more cursor holes, both now fixed: a `since` at the top of the number range overflowed checked arithmetic inside the replay helper and threw after the SSE headers were already flushed, and a cursor ahead of the ring (a reconnect after a restart) produced an empty replay and then filtered out every future event, so the feed was open but permanently silent — the cursor is now clamped to the ring head. It also found that a full feed was enforced by an exception thrown after the response had committed to SSE headers (fixed with a pre-flight 503 and a refusal that is a return value), and that the tests only ever exercised replay rather than the handover window: there is now a concurrent-producer ring test that asserts every sequence number arrives exactly once, and a feed test that subscribes before the flood. What it found that is NOT fixed stays open and is stated in SPEC rather than papered over: an ended feed says "reconnect", not "N events were lost"; an ended feed is indistinguishable from one that finished normally; a filtered reader cannot see that a terminal event was evicted from the ring; and the ring has no epoch, so a restart is not distinguishable from a quiet period. Those need a wire decision, and they are the shape of the remaining work.

Moved out of In Progress: the replay slice is merged, and the remaining ticket-failure-path work is gated by nothing external — it waits for attention, so it belongs in the queue with its plan attached rather than in the WIP column.

Prerequisite check on the two open criteria (board audit, no status change). AC #2 says it waits on the switch identity described as TASK-10 plan step 3 — immutable (target, epoch, completion) state that a completion can be validated against. TASK-10 is now Done, and the identity is not in the tree: the runtime holds one switch state per engine carrying model, profile and start time only, and nothing in the source names an epoch at all. SPEC 14.1 row 3 calls switch serialization and the error contract closed, and its "epoch" row (row 6) is the event-feed epoch awaiting the live-view decision on TASK-21 — a different thing. So the prerequisite AC #2 depends on is real, exists nowhere in the code, and is not owned by any open task. Flagging it, not deciding it: whether that step becomes a TASK-2 step or its own task is the code owner's call. Until someone owns it, AC #2 is not merely unstarted, it is un-buildable.
<!-- SECTION:NOTES:END -->

## Status notes
The replay slice's external review found six gaps, each verified against the code at that commit and all of them fixed in the next slice rather than left as notes: replay is snapshotted before the live subscription starts, so an event appended in that window reaches neither path, and the per-frame SSE flush widens the hole; replay caps at 1024 events while the ring holds 4096, so a reconnecting client can be silently missing a thousand events it is entitled to; the subscriber buffer drops with `DropWrite` and no counter, so a dropped terminal event can be invisible to a reader that is already filtering; a `since` cursor ahead of the ring makes the replay helper compute a negative count and throw after the SSE headers are already sent; the per-subscription channel is never completed and is not linked to shutdown, so a reader of an unknown ticket stays subscribed and the subscriber list is uncapped; and the tests open the feed only after requests finish, which proves replay isolation but never exercises the handover window. Two related items are decisions rather than fixes: a ticket-scoped reader cannot see the engine-switch events it is promised because switch events carry no ticket id, and the docs' promise that a gap in `Seq` always reveals loss is true only for an unfiltered feed. Both are recorded as acceptance gaps to settle with the fix.

**Correction:** the note here previously claimed `GET /admin/tickets/{id}/events`,
`POST /admin/tickets/{id}/cancel` and a ticket-scoped replay test had landed. They had not: the surface
is `GET /admin/tickets/{id}` (read) and `DELETE /admin/tickets/{id}` (cancel), `RouterEventFilter` has
no ticket field, and `AgentSurfaceTests` pins the tool manifest and `X-Inference-Ticket`, not replay.
AC #1 is unchecked again. What is actually shipped: `X-Inference-Ticket` on every admitted response
(including handled failures), so a caller can reach the two ticket routes without scraping the stream.

AC #1 closed as shipped: `GET /admin/tickets/{id}/events?since=<seq>` plus `ticket=` on
`/admin/events`, both filtering replay **and** live tail. `EventSurfaceTests` proves isolation with two
concurrent tickets (a frame carrying the other ticket id fails the test) and pins the admin feed's
PascalCase field names. Fixing this surfaced two defects in the library's own subscriber:
`SubscribeEvents` replayed the ring unfiltered, and its single-slot waiter dropped any event that
arrived while the consumer was busy; subscribers now have a bounded 256-event buffer (drops are visible
as `Seq` gaps) and SSE headers flush before the first frame, so an unknown ticket is a quiet 200.

Still open: #2 needs the switch identity (`(target, epoch, completion)`, TASK-10 plan step 3);
`/v1/models` still lacks AIR model metadata; the `POST /admin/tickets/{id}/cancel` alias remains
SPEC §13.3 work. #3 is partly met — wire tests cover the replay additions and `ServerWireTests` keeps
the OpenAI-compatible surface pinned — and stays open for additions that do not exist yet.

Still open, and why:

- #2 needs the switch identity the validation is checked against — that is TASK-10 plan step 3
  (immutable `(target, epoch, completion)` switch state). Validating against today's mutable
  per-target flag would pass the test and still mis-attribute a switch.
- `/v1/models` does not yet carry AIR model metadata (context window, slots, availability), and the
  Pi-side half (session extension) is a different repo entirely.

## Links

- Surface still owed: [SPEC §13.3](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1157) — the delta list is the spec of this task.
- Admin routes as mapped today: [ServerApp.cs:51-57](https://github.com/ArborealML/Inference-Router/blob/main/src/Arboreal.InferenceRouter.Server/ServerApp.cs#L51-L57) — read, cancel, and both event feeds.
- Tests to extend: [EventSurfaceTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/EventSurfaceTests.cs) for ticket filtering, [ServerWireTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/ServerWireTests.cs) for the OpenAI-compatible surface.
- Protocol write-up to keep honest: [docs/ai-integration.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md).
