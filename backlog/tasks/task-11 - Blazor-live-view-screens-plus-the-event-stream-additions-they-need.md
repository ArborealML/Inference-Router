---
id: TASK-11
title: 'Blazor live view: screens plus the event-stream additions they need'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-17T16:01:00Z'
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
Wakes on TASK-21: the three postures are adopted and the remaining act is writing them into SPEC, which the coding session owns. Work here resumes when that SPEC revision lands.

Blocked-state check: the blocker is a person, and the human task that carries it is TASK-21 (audience, credential posture, and whether the event feed gains a loss event). The two questions restated in this task's body are context for whoever reads this task — the answers belong on TASK-21, so answering here does not move anything.

Wake condition, stated for the Waiting column: this task wakes when the live-view decisions are recorded (TASK-21 — audience, credential posture, and whether the event feed gains an explicit loss event), because the screens cannot be specified until the feed they read from is specified. Per the working contract the feed-contract plan also gets an external review before the code exists, so the second wake is that review landing. What is true now: TASK-21 is a BLOCKED owner task again - the external design recommendation landed and the three answers (audience, credential posture, and whether the feed gains a loss event) are ratifications only the owner can make. So this task does wait on a person, and the Waiting column is correct only because that person's task exists. The earlier sentence claiming nothing here waits on a person was written when the questions were still with an agent, and is withdrawn.

Moved out of the Description under decision-18's word line — nothing deleted, nothing rewritten:

1. **Where does it come from?** Serve `air-ui` as static files from the router itself (one binary, one
   origin, no CORS, but the router then ships a UI bundle and must serve a second content type), or as a
   separate static site the operator hosts anywhere (cleaner separation, but then the browser needs a
   reachable admin feed and CORS/origin policy). Recommendation: serve it from the router for v1 — one
   process, same auth boundary, no new infrastructure — and keep the admin feed the only contract.
2. **Who can see it?** `/admin/*` is currently unauthenticated and bind-address-only (§6.2). A live
   dashboard makes that difference visible: an operator who exposes the router on the tailnet is
   exposing the admin feed too. Options: keep bind-address-only and document it loudly, or add a
   separate admin token. Recommendation: keep bind-address-only for the router itself and let the
   operator expose the UI behind a reverse proxy; add the admin token only when someone needs a
   remote admin. Say otherwise and I will do the token first.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:02:00Z

---

**Correction, and the board was wrong.** A comment here a moment ago claimed this task's wake had already happened and moved it to To Do. It has not, and the claim came from reading the board listing instead of the predecessor task. The predecessor TASK-21 records its three postures as **adopted**, but its remaining act is still open: writing them into SPEC as a revision with the revision-log line in the same commit — checked here, `SPEC.md` carries no operator-first posture text at b214ce6. **Wake, named:** that SPEC revision landing, after which TASK-21 closes and this starts.

---

<!-- COMMENTS:END -->
