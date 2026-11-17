---
id: TASK-11
title: 'Blazor live view: screens plus the event-stream additions they need'
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-17T14:02:00Z'
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

**Blocked on one decision (owner)**
The live view is a Blazor WASM app (SPEC §12.5). Two questions decide its shape, and both are product
calls rather than technical ones:

Nothing in this task is technically blocked; it is these two calls.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ticket records expose owner, state, priority, queue, attempt, active profile, required/active context, capacity, timing, result, prompt/completion bytes, tokens, prefix hash, config pin and commit boundary
- [ ] #2 Events cover request lifecycle, rejection, capacity change, queue, health/crash/restart, switch/profile and prefix. Whether the feed also gains an explicit stream-gap event is answered by TASK-21, whose incoming recommendation is NOT to add one and to derive continuity client-side; this criterion does not decide it
- [ ] #3 /admin/status reports active/queued/readiness/rates; /admin/events supports filtering, heartbeat and bounded sequence-gap handling; a bounded /admin/requests exists
- [ ] #4 Admin JSON schema and casing pinned once (SPEC 15.1) and covered by contract tests
- [ ] #5 Screens render from the event stream alone: global inference, queues/admission, engines/profiles/switches, topology/trends, request timeline, health/config/security
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked on TASK-21 — the next move is the live-view audience, credential and wire-extension decisions, which belongs to a human. Work here resumes when that task is closed.

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
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T10:14:00Z

---

AC #2 rewritten: it used to list an explicit stream gap event as if settled, which pre-decided a wire question that TASK-21 - now a Blocked owner task - exists to answer. The task still depends on TASK-21 in front matter, so the gate is visible; the contradiction was in the criterion, not the dependency. TASK-6 re-checked for the stale macOS wording and none is present (no TASK-22 reference in the file).

---

created: 2026-11-17T10:58:00Z

---

Wake, named: the three posture answers on [TASK-21](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-21%20-%20Decision-what-the-live-view-shows-who-may-open-it-and-whether-the-wire-gains-loss-reporting.md) are adopted (operator-first; `/admin` unauthenticated and loopback-only with operator-managed tunnels; no new feed event kind — continuity computed client-side from sequence holes plus the exclusive cursor). This task starts when SPEC carries that wording, because AC #5 is the event-stream half of it.

---

created: 2026-11-17T13:56:00Z

---

**Stale park, found by the board audit at b214ce6.** This task sat Waiting on posture answers from a predecessor that is now closed; the wake already happened, so the Waiting column was hiding ready work. Moved to To Do with its assignee unchanged. Nothing about its scope or criteria changed.

---

created: 2026-11-17T14:02:00Z

---

**Correction, and the board was wrong.** A comment here a moment ago claimed this task's wake had already happened and moved it to To Do. It has not, and the claim came from reading the board listing instead of the predecessor task. The predecessor TASK-21 records its three postures as **adopted**, but its remaining act is still open: writing them into SPEC as a revision with the revision-log line in the same commit — checked here, `SPEC.md` carries no operator-first posture text at b214ce6. **Wake, named:** that SPEC revision landing, after which TASK-21 closes and this starts.

---
<!-- COMMENTS:END -->
