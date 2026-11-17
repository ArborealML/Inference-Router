---
id: decision-4
title: 'Board columns: Waiting, and a work-in-progress cap'
date: '2026-11-17T02:20:00Z'
status: accepted
---
## Context

Four tasks sat in `In Progress` for hours while nothing was being implemented. The column carries two
different meanings: "someone is changing repo state for this" and "this is mine". The convention that an
external review in flight does **not** make a task In Progress was right, but it left the task nowhere to
go, so it stayed put. The board then reads as dead even while reviews are landing and code is going in —
which is exactly the signal a kanban column is supposed to give.

A column named after an outside actor ("in the review service") was considered and rejected: it names who
is involved rather than what happens next, and it would hold both "review in flight, I have nothing to
do" and "review landed, I must act", which are opposite states.

## Decision

Columns are `To Do → In Progress → Waiting → Blocked → Done` (`backlog/config.yml`).

- **To Do** — not started. An assignee means it is claimed, not that it is moving.
- **In Progress** — repo state is being changed for this right now. **One at a time**; two only
  briefly, and never for the whole day.
- **Waiting** — work started, parked on an external event that will wake it: a review, a CI run, a pull
  request gate, a predecessor task. The task body names the event that wakes it.
- **Blocked** — no event will wake it. It needs a human decision or permission before anything can
  happen (an approval, a scope call, runner minutes). This keeps `Blocked` meaning *a person must move*.
- **Done** — acceptance criteria verified, last responsible actor as assignee.

Two rules that make the columns worth having:

1. **The board is updated at each work boundary** — every commit or push that changes what the next move
   is. Not at end of day, and not only when a task finishes.
2. **Anything `Waiting` for a week without being woken is re-triaged**: dispatch it, descope it, or park
   it out loud. A column that never changes is a bug in the board, not a fact about the work.

## Consequences

- `In Progress` becomes a claim on attention rather than on ownership, so it can be read as WIP.
- Owner decisions are visible as `Blocked` instead of hiding among things that will wake themselves,
  which is where the "board is static" complaint mostly came from.
- `Waiting` requires naming the wake condition, which forces the honest question — is something actually
  going to wake this, or is it parked because nobody decided?
