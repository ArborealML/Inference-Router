---
id: TASK-11
title: 'Blazor live view: screens plus the event-stream additions they need'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-18T10:11:00Z'
labels:
  - ui
milestone: v0.10 - live view and robustness parity
dependencies:
  - TASK-9
  - TASK-21
priority: medium
ordinal: 11000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build the Blazor live view on the event stream, taking the good parts of PAIR's UI (workload
rail, filters, topology, endpoint copy, actionable errors) and dropping the appliance parts
(installer, model deletion, pairing, autostart/update/wipe). Our screens differ: global inference
activity, queues and admission, engines/profiles/switches, topology and trends, a request timeline,
and health/config/security. The event stream must carry the fields these screens need before any of
them can be built, so the stream work comes first.

**Links**
- Feed the screens read from: [SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L946). Admin surface and error casing: [SPEC §6](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L635).
- Borrow/change/skip list: [SPEC §12.5](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1079).
- Endpoints that exist today: [ServerApp.cs:51-57](https://github.com/ArborealML/Inference-Router/blob/main/src/Arboreal.InferenceRouter.Server/ServerApp.cs#L51-L57).
- Gate this has to satisfy: [SPEC §14.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1208). Decision that wakes it: [TASK-21] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-21%20-%20Decision-what-the-live-view-shows-who-may-open-it-and-whether-the-wire-gains-loss-reporting.md).

**Do this (about 1 minute)**
Nothing to decide here — the three questions this task waits on are written out on **TASK-21** with the
answer format, in [TASK-21](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-21%20-%20Decision-what-the-live-view-shows-who-may-open-it-and-whether-the-wire-gains-loss-reporting.md). Answer those and this one starts on its own.

**Wake: TASK-21's SPEC revision** (the live-view postures are adopted; the SPEC text that records them is the coding session's act, so no person is in this path)
The live view is a Blazor WASM app (SPEC §12.5). Two questions decide its shape, and both are product
calls rather than technical ones:

Nothing in this task is technically blocked; it is these two calls.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ticket records expose owner, state, priority, queue, attempt, active profile, required/active context, capacity, timing, result, prompt/completion bytes, tokens, prefix hash, config pin and commit boundary
- [ ] #2 Events cover request lifecycle, rejection, capacity change, queue, health/crash/restart, switch/profile and prefix. Whether the feed gains an explicit stream-gap event is TASK-21's answer, not this task's
- [ ] #3 /admin/status reports active/queued/readiness/rates; /admin/events supports filtering, heartbeat and bounded sequence-gap handling; a bounded /admin/requests exists
- [ ] #4 Admin JSON schema and casing pinned once (SPEC 15.1) and covered by contract tests
- [ ] #5 Screens render from the event stream alone: global inference, queues/admission, engines/profiles/switches, topology/trends, request timeline, health/config/security
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Wake, as events:** the SPEC revision that records the three live-view postures (adopted already; writing them into
SPEC is the coding session's act on [TASK-21](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-21%20-%20Decision-what-the-live-view-shows-who-may-open-it-and-whether-the-wire-gains-loss-reporting.md)),
and the external review of the feed-contract plan landing before the code exists.

**Open design question this task must answer first:** serve `air-ui` as static files from the router itself (one
binary, one origin, no CORS; the router then ships a UI bundle and serves a second content type), or as a separate
static site the operator hosts anywhere (cleaner separation, another thing to deploy). The choice changes the packaging
criterion, so it is decided in this task's plan, not in code.

Superseded log entries, including the older wake claims: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).

Cost read at 64b9f94, not run: EventRing.Append hashes an over-budget value while holding the ring lock, and RouterEventFilter re-hashes an over-budget query on every replayed or live event. Bounded by the ingress cap today, so no row of its own yet — but a live view subscribes and filters constantly: normalize inside Append outside the lock, and prepare normalized filter fields once per subscription.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
