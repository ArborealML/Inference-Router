---
id: TASK-21
title: >-
  Decision: ratify the live-view shape - audience, credential posture, and no
  new feed event kind
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T10:58:00Z'
labels:
  - ui
  - wire
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

**Do this (3 minutes).** Three one-line answers, on this task: `1 operator-first`, `2 loopback plus tunnels`, `3 no gap event`. Any answer can be replaced with yours and the reason. Settings or commands are not involved - these are product and wire postures, and answer 3 is the only one that touches the wire (the recommendation is to change nothing).

**Answer format (one line each):** `1 operator-first`, `2 loopback+tunnels`, `3 no gap event` — deviations welcome,
and each deviation names what it buys.

**Links**
- What the admin surface is today: [SPEC §6](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L635), [§6.1](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L635).
- The feed the answers describe: [SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L946); screens: [SPEC §12.5](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1079).
- Task that starts when this is answered: [TASK-11] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-11%20-%20Blazor-live-view-screens-plus-the-event-stream-additions-they-need.md).
- Adjacent and deliberately separate: [TASK-34] (https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-34%20-%20Decision-what-the-caller-sees-when-a-generation-stalls-or-a-client-stops-reading.md) — what a *caller* sees when a generation stalls, which is a `/v1` question, not a feed question.

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
the three questions this task posed (dispatched and answered 2026-11-17, transcript kept in `.local/reviews/`, not
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

DISPATCHED to the external research and review service at 2026-11-17 08:07 UTC, reasoning-only authority, top reasoning tier, text deliverable. The brief put to it: the three questions above, with the feed semantics it has to respect (bounded ring, exclusive replay cursor, slow subscriber ended not pruned, subscribers bounded, 503 before SSE headers) and the AOT/no-reflection constraint. Wake condition: the recommendation lands in this session, and it is expected as three recommendations each with its strongest counter-argument. Request bookkeeping (id, hash, tier) lives in .local/reviews/dispatches.md, not on the board. When it lands: review against how this repository's admin surface and feed actually behave, then file one owner task with the approval lines written out.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T07:54:00Z

---

Assignee and title changed on purpose. This is not an owner decision being handed to a machine: it is a design question whose answer is a recommendation, so the external review service owns the analysis and the board session owns what happens to it - review against how this repo's admin surface and event feed actually work, then file one owner task with the approval lines written out. Wake condition: the recommendation lands. Until then no person is waiting on this and no person is being asked to write a design. Retitled from Decision: to Design: so the title matches who acts.

---

created: 2026-11-17T08:14:00Z

---

Design phase closed: dispatched to the external research and review service at 08:07 UTC and answered at 08:1x UTC (transcript in .local/reviews/, not in the repository). The board reviewed the answer against SPEC 6/10/12.5 and the feed semantics in code before putting any of it here - in particular the claim that loss is subscriber-relative, which is true of this ring: eviction is global, consumption is per subscriber, and an eviction event would itself consume ring capacity to describe something that may affect nobody. Retitled Design: back to Decision: because what remains is ratification, not analysis. Status Blocked: no event wakes this, a person answers three one-line approvals (about 3 minutes).

---

created: 2026-11-17T10:58:00Z

---

Reassigned under decision-16 and **adopted as recommended** by the external design answer, which the board checked against this code before recording it: (1) the live view is operator-first, not multi-tenant; (2) `/admin` stays unauthenticated and loopback-only, with operator-managed tunnels plus browser-request hardening — no new credential surface; (3) **no gap/loss event kind on the shared feed**: compute continuity client-side from sequence holes plus the exclusive cursor, and if a server-side notice is ever wanted it must be subscriber-relative and must not consume a global sequence id. Remaining work: write those three into SPEC §6, §10 and §12.5 as posture, with a revision-log line, then [TASK-11](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-11%20-%20Blazor-live-view-screens-plus-the-event-stream-additions-they-need.md) can start. Answer 3 changes nothing on the wire, which is the point of adopting it.

---
<!-- COMMENTS:END -->
