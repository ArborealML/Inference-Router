---
id: TASK-24
title: 'Decision: build the agent event inbox or defer it (design delivered)'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T02:30:00Z'
updated_date: '2026-11-17T08:14:00Z'
labels:
  - tooling
  - notify
dependencies: []
priority: medium
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**You are being asked to say build it or defer it.** The design this task asked for exists: the external research
and review service returned it (dispatched and answered 2026-11-17; transcript in `.local/reviews/`, not in the
repository). The board reviewed it against the two push paths this machine already runs and against who actually
executes here. What is left is one answer, about 2 minutes.

**What the design is, in short.** A narrow durable **topic inbox**, not a broker: an append-only store outside the
repository (a SQLite file in durable user state), one poller per outside fact, a publisher registry so an event
carries an authenticated publisher id rather than a self-claimed one, per-event acknowledgement with leases so a
crash between waking and acknowledging replays instead of losing the fact, retention until every live subscription
has acknowledged plus a short replay window, and budgets with a degraded flag instead of silent deletion. First
source to prove it: **a merged pull request**, with an event id built from repo, PR number and merge SHA so
repeated polls cannot produce two work items, and "closed" not treated as "merged".

**The board's own reading, so you are not choosing between a design and nothing.** The strongest argument against
building it is the review's own, and it applies here more than anywhere: this box already has two working wake
paths (the research service pushing results, and session-to-session messages), plus a scheduled check could answer
"what merged since I last looked" on demand. And the isolation the authority model depends on does not exist on
this machine — every agent session and every poller runs as the **same OS user**, so per-publisher token files
protect against accident, not against a peer that can read the token. Real protection would mean a separate service
account, which is the part that turns a script into an installation.

**Recommendation: defer, with a written trigger.** Revisit when any one of these happens: (a) a second,
independently-sourced topic is actually wanted, not imagined; (b) a fact has been *missed* because no push path
covered it; (c) wake-spam or duplicate waking shows up in whatever ad-hoc polling gets bolted on instead. Building
the PR-merge slice today buys something the existing research path can already do on demand, at the cost of a
subscription and garbage-collection lifecycle to maintain.

**Answer format (one line):** `defer until trigger`, or `build the merged-PR slice` — or `build it under account X`
if you want the isolation to be real rather than nominal, and name the account.

**Links**
- What it would generalise: [docs/ai-integration.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md), [SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L946).
- The board it would publish about: [backlog/](https://github.com/ArborealML/Inference-Router/tree/main/backlog/).
- Nothing on the board waits on this task, so deferring costs no work; it only keeps an idea from being quietly forgotten.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: defer until a named trigger, build the merged-PR slice, or build it under a named service account so the publisher isolation is real
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Board audit: retitled from Design: to Decision: because the assignee is the owner and the only thing outstanding is their three answers — the working contract reserves the Design/plan shape for work someone executes, and human tasks for decisions. AC #4 used to read as an implementation step ("one real source is wired end to end"), which this task can never close by itself: the wiring is ordinary engineering once the answers land, and it becomes its own task with its own plan. What it now asks is the choice that has to precede that work.

What it unblocks: the first slice of the bus (publisher, store, one subscription), which is engineering work that starts the moment ACs 1-3 are answered; nothing on the board is blocked in the meantime, because today the same effect is obtained by hand — a research service that pushes results, a session-to-session message channel, and a human passing word along.

DISPATCHED to the external research and review service at 2026-11-17 08:07 UTC, reasoning-only authority, mid reasoning tier, text deliverable. The brief put to it: authority model, store location and retention, and one first source to wire, with the two push paths that already exist named so the answer cannot casually duplicate them, and single-machine operational cost treated as a first-class criterion. Wake condition: the design lands. Then the board session reviews it against those existing paths, throws out what assumes infrastructure this box does not have, and files one owner approval task. Request bookkeeping lives in .local/reviews/dispatches.md, not on the board.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Moved to the external service per the owner instruction: this tasks deliverable is a design with options — authority models for a publish/subscribe store, retention while no agent runs, and which first source proves the path — which is what that service is for, and the contract wants a plan reviewed before the code exists. Handback is the design plus answers to ACs 1-3; the first wired source (AC #4) stays engineering work for TASK-28 or this task once the shape is fixed.

---

created: 2026-11-17T07:54:00Z

---

Same treatment as TASK-21: the assignee is the external service because the deliverable is a design, and the board session reviews it and then files the one approval a person actually owes - where a durable store may live on this machine and what may write to it. Wake condition: the design lands. Retitled Decision: to Design: so the title does not claim an owner decision that has not been asked yet.

---

created: 2026-11-17T08:14:00Z

---

Design phase closed: dispatched 08:07 UTC, answered 08:1x UTC (transcript in .local/reviews/). The board's recommendation is DEFER, and the reason is a fact about this machine rather than a preference: every agent session and every poller here runs as the same OS user, so per-publisher token files guard against accident and not against a peer that can read the token - the isolation the authority model assumes does not exist unless a separate account is created. The review itself names the same counter-argument: two working push paths plus a scheduled check cover a handful of facts. Status Blocked: a person decides build-or-defer; nothing on the board waits on it, so deferring costs no work.

---
<!-- COMMENTS:END -->
