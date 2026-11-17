---
id: TASK-24
title: 'Decision: who may publish to the event bus, and where its store lives'
status: To Do
assignee:
  - '@chatswarm'
created_date: '2026-11-17T02:30:00Z'
updated_date: '2026-11-17T05:55:00Z'
labels:
  - tooling
  - notify
dependencies: []
priority: medium
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->

The gap: things that happen outside this session — a pull request merging, a decision landing on the board, a build finishing elsewhere, a machine coming back up — only reach me when a human tells me. The shape being proposed: a small durable event store with named topics; sources (scripts, services, a cron that checks a thing and emits when it changed) publish events, and an agent subscribes to the topics it cares about and gets woken the way it is woken today by asynchronous research results. Publisher identity and topic ownership matter, because otherwise anything can claim anything. What is needed from the owner is the authority model and where the store lives; the poller scripts and the subscription plumbing are ordinary work once the shape is fixed. Today the same effect is hand-rolled per case: a research service that pushes results, a session-to-session message channel, and a human passing word along.

**Links**
- Push paths it would generalise: [docs/ai-integration.md](https://github.com/ArborealML/Inference-Router/blob/main/docs/ai-integration.md), [SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L946).
- Surface a decision landing would publish: [backlog/](https://github.com/ArborealML/Inference-Router/tree/main/backlog/).

**Do this (about 5 minutes)**
Three answers, and I can build the first slice:

1. **Who may publish, and how does a publisher prove it?** Options: a shared secret per topic; a token
   file the poller script already has; or "whatever user owns the store may publish anything" for a
   single-machine setup.
2. **Where does the store live, and what happens to events while I am not running?** A file under the
   project directory, a local daemon, or the same transport the research notifications already use.
   Retention matters: an event nobody consumed must not vanish.
3. **What is the first source to wire?** Pick one and I will make it the proof: a merged pull request,
   a board decision landing, or a machine coming back up.

Answer format: `TASK-24: shared secret / file store / merged PR`. A written design is welcome instead;
I will review it against how this repo's notification paths actually work.

<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The authority model is stated: who may publish to a topic, and how a publisher proves it
- [ ] #2 The store location and retention are decided, including what happens to events while no agent is running
- [ ] #3 A first transport is chosen (the existing push path for research results, or a new one)
- [ ] #4 The first source to wire end to end is named: a merged pull request, a board decision landing, or a machine coming back up
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Board audit: retitled from Design: to Decision: because the assignee is the owner and the only thing outstanding is their three answers — the working contract reserves the Design/plan shape for work someone executes, and human tasks for decisions. AC #4 used to read as an implementation step ("one real source is wired end to end"), which this task can never close by itself: the wiring is ordinary engineering once the answers land, and it becomes its own task with its own plan. What it now asks is the choice that has to precede that work.

What it unblocks: the first slice of the bus (publisher, store, one subscription), which is engineering work that starts the moment ACs 1-3 are answered; nothing on the board is blocked in the meantime, because today the same effect is obtained by hand — a research service that pushes results, a session-to-session message channel, and a human passing word along.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:55:00Z

---

Moved to the external service per the owner instruction: this tasks deliverable is a design with options — authority models for a publish/subscribe store, retention while no agent runs, and which first source proves the path — which is what that service is for, and the contract wants a plan reviewed before the code exists. Handback is the design plus answers to ACs 1-3; the first wired source (AC #4) stays engineering work for TASK-28 or this task once the shape is fixed.

---
<!-- COMMENTS:END -->
