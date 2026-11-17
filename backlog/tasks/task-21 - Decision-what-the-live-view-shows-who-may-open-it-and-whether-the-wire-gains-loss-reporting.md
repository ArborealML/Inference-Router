---
id: TASK-21
title: >-
  Decision: ratify the live-view shape - audience, credential posture, and no
  new feed event kind
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T16:01:00Z'
labels:
  - ui
  - wire
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Adopted; nobody is being asked anything: 1 operator-first, 2 `/admin` unauthenticated and loopback-only with
operator-managed tunnels, 3 no new feed event kind — continuity comes from sequence holes plus the exclusive
cursor.** The external design answer recommended exactly this, and the board checked it against [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md)
(§6, §10, §12.5) and against the ring's eviction semantics in code before recording it.

**Remaining act (the coding session, about 30 minutes):** write those three postures into SPEC as a revision, with
the revision-log line in the same commit, then start [TASK-11](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-11%20-%20Blazor-live-view-screens-plus-the-event-stream-additions-they-need.md), which was parked behind this answer.

**Why item 3 is the load-bearing one:** it is a decision *not* to change the wire. A gap or loss event kind on the
shared feed would consume a global sequence id to describe something that may affect nobody — eviction is global,
consumption is per subscriber, so loss is subscriber-relative. If a server-side notice is ever wanted it must be
subscriber-relative and must not consume a sequence id.

**Links:** [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §6 (the admin surface and its error casing), §10 (the feed), §12.5 (the screens); the
adjacent and deliberately separate `/v1` question: [TASK-34](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-34%20-%20Decision-what-the-caller-sees-when-a-generation-stalls-or-a-client-stops-reading.md) (answered).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The audience and the credential posture are stated
- [ ] #2 The loss-reporting event kind is approved or declined, with a one-line reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reasoning and evidence moved out of the description under the answer-first rule (decision-15):

**You are being asked to ratify a design, not to write one.** The external research and review service answered
the three questions this task posed (dispatched and answered 2026-11-17, transcript kept in `the board's untracked review transcripts`, not
in the repository). The board checked each answer against how this repository's admin surface and event feed
actually behave before putting it here. What is left is three one-line approvals, and one of them prevents a wire
change rather than causing one. Cost: about 3 minutes.

**Recommendation 1 — audience: operator dashboard first.** The feed as it exists reconstructs routing choices,
engine-state transitions and terminal outcomes *within retention*; it does not reconstruct a request trace. An
"agent debugger" screen would promise causal stages the feed never emitted, and evicted transitions cannot prove
current engine state, so any live view has to bootstrap current state from the admin reads and label the rest as
observed. Cheapest first ship: a read-only view over the existing admin reads plus the SSE feed, counts scoped to
retained events, no new event kinds. **If you agree:** TASK-11 starts with an operator overview plus request
drilldown limited to what the feed holds. **If you want the debug surface too:** it is new event and field
contracts (stages, timings, correlation ids), which is a wire addition with its own approval.

**Recommendation 2 — credential posture: keep `/admin` unauthenticated and loopback-only; do not put a token on it.**
The view is co-located and reached over an operator-managed localhost tunnel. A single admin token would authorise
engine start/stop as much as viewing it, so a stolen token is control, not visibility; and a separate read-only
credential is only honest if there is an independently read-only endpoint to put it on, which does not exist today.
The part worth doing now is browser-request hardening on a loopback control plane — Host/Origin checks and safe
methods, because a hostile page can otherwise make the browser hit `localhost` — and that is not a wire change.
**If you agree:** no credential work, and any future remote or multi-user access comes back as its own approval
(TLS plus an authenticated read-only surface, separate from the mutating one).

**Recommendation 3 — do NOT add a loss/gap event kind to the feed.** This is the one that touches the wire, and the
recommendation is to decline it. Reasoning worth having in front of you: loss is *subscriber-relative*, so an
"eviction" event published into the shared ring describes something that may affect nobody while consuming ring
capacity itself; a viewer can already derive "N global events not observed" from sequence holes plus its own
exclusive cursor; and a contiguous global id sequence means a hole counts *events*, not "routing decisions", so
the precise claim you wanted the view to make is not the claim the number supports. A server-side notice done
properly would be computed per subscriber at replay handover, delivered outside the shared ring, and would not
consume a global sequence number — real protocol and state complexity, for a badge. **If you agree:** no feed
change, TASK-30's wire surface stays exactly as it is, and the live view shows a continuity badge with an
"incomplete history" qualifier instead. **If you decline the recommendation:** it becomes a versioned feed change
with per-subscriber state and replay races, and it needs its own task and SPEC section.

Read the decisions against SPEC §6.1 (admin surface: unauthenticated, loopback bind) and §10 (feed shape, replay, subscriber bounds): https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md — those two sections are the whole evidence base for the audience and credential questions, and §6.1 plus the §6.1 casing table is what a new event kind has to be added through. Per the owner instruction this decision is now the agents to take and record as a decision record; the wire addition is still a §6 change with contract tests, so the loss-event half lands as code only with a decision record naming it. TASK-11 wakes when that record exists.

DISPATCHED to the external research and review service at 2026-11-17 08:07 UTC, reasoning-only authority, top reasoning tier, text deliverable. The brief put to it: the three questions above, with the feed semantics it has to respect (bounded ring, exclusive replay cursor, slow subscriber ended not pruned, subscribers bounded, 503 before SSE headers) and the AOT/no-reflection constraint. Wake condition: the recommendation lands in this session, and it is expected as three recommendations each with its strongest counter-argument. Request bookkeeping (id, hash, tier) lives in the board's untracked review log, not on the board. When it lands: review against how this repository's admin surface and feed actually behave, then file one owner task with the approval lines written out.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:02:00Z

---

Wake named explicitly for the task that parks behind this one (TASK-11, Waiting): **the SPEC revision that writes the three adopted postures into SPEC §6, §10 and §12.5**, with its revision-log line in the same commit. Measured at b214ce6: `SPEC.md` has no operator-first or `/admin`-tunnel posture text, so the act is not done and TASK-11 must not start. The adoption itself is recorded in the comment above; AC #1 and #2 stay unchecked until the SPEC revision exists, because this task is graded on the record, not on the recommendation.

---

<!-- COMMENTS:END -->
