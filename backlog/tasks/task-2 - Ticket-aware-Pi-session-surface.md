---
id: TASK-2
title: Ticket-aware Pi session surface
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-18T20:30:00Z'
labels: []
milestone: m-7
dependencies: []
priority: medium
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the remaining ticket-aware pi contract from SPEC section 13.3. TASK-110 supplies a fenced host epoch and TASK-118 binds delivery to a verified serving instance. This task owns the client lease identity, expiry and atomic single consume at dispatch. Ticket-filtered replay is already shipped.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Pi can replay only the events for its own ticket
- [ ] #2 A ready ticket is validated against the switch that produced it
- [ ] #3 Wire tests cover each addition and the OpenAI-compatible surface stays unchanged
- [ ] #4 Expired, cancelled, replayed or superseded ready tickets cause zero upstream writes. A valid ticket can be consumed once against its verified instance, even with concurrent duplicate submissions.
- [ ] #5 Cold-target discovery distinguishes configured capacity from observed residency and serving context. A catalog-only model cannot be presented as currently loaded or as having a verified serving window.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
The ticket half.

1. Ready-ticket validation at inference time (atomic lease consume) — needs the switch identity above.
2. `/v1/models` with `air:{engine,profile,availability,loaded}`; the §13.3 delta list is this task's spec.
3. `POST /admin/tickets/{id}/cancel` alias — today cancellation is `DELETE`.
4. `ServerWireTests` keeps the OpenAI-compatible surface pinned.

Landed, not to be redone (rules now in SPEC §10): exclusive replay cursor; replay cap = ring capacity 4096; snapshot and live subscription under one ring lock; cursor clamped to the ring head, checked arithmetic; a full feed refused `503 too_many_subscribers` before any SSE header, refusal a return value not an exception; 256-event subscriber buffer; a consumer that falls behind has its feed ended; live subscribers bounded; feeds end at shutdown; every terminal failure path completes the admitted channel, and a caller abort stays `cancelled` (`StreamingFailureTests`).

Out of scope: per-subscriber drop accounting and `stream.gap` — a new event kind is a wire decision (TASK-21 → TASK-11), not a local fix.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Shipped and pinned:** `GET /admin/tickets/{id}/events?since=<seq>` and `ticket=` on `/admin/events` filter replay **and** live tail — routes in `ServerApp.cs`. `EventSurfaceTests` proves isolation with two concurrent tickets and pins the admin feed's PascalCase names; `AgentSurfaceTests` pins the tool manifest and `X-Inference-Ticket`, which rides every admitted response, including handled failures. Fixing it surfaced two subscriber defects, both since fixed. SSE headers flush before the first frame, so an unknown ticket is a quiet 200.

**Open, and why:**
- AC #2 has no identity to validate against; validating against today's mutable per-target flag would pass the test and still mis-attribute a switch.
- `/v1/models` has no AIR metadata (context window, slots, availability); the Pi-side session extension is another repo.
- The `POST` cancel alias is still owed to SPEC §13.3.
- AC #3 is partly met — replay additions and the OpenAI surface are covered — and stays open for additions that do not exist yet.
- Unrepaired on purpose, pending a wire decision: an ended feed says "reconnect", not "N events lost"; it looks like one that finished normally; a filtered reader cannot see a terminal event evicted from the ring; the ring has no epoch, so a restart looks like a quiet period ([SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md)).

**Tests to extend:** [EventSurfaceTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/EventSurfaceTests.cs), [ServerWireTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/ServerWireTests.cs). Protocol write-up to keep honest: [docs/ai-integration.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md).

Superseded log entries — the premature "AC #1 landed" claim and its correction, both review passes as narrative, the In Progress move, the decision-18 Description move — at [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->
