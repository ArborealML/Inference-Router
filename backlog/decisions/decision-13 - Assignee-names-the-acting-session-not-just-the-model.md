---
id: decision-13
title: 'Assignee names the acting session, not just the model'
date: '2026-11-17T07:00:00Z'
status: accepted
---
## Context

The working contract requires an assignee on every task and says to name the model doing the work, so a
hand-off is attributable. Two sessions now work in this repository on the same model, so the handle
`@qwen3.8-flash-next` no longer says which of them owns a task — TASK-2, 6, 20, 21, 23 and 27 were all
attributable to "the model", which is exactly the ambiguity the rule exists to remove. The purpose is
attribution when someone has to pick the work back up after a crash, not the exact model string.

## Decision

Two handles, both naming the model plus the role of the session that acts:

- **`@qwen3.8-flash-next`** — the session that changes repository state: implementation, tests, SPEC, the
  release surface. Tasks 15, 25, 26, 28 and anything that touches `src/`, `tests/` or `SPEC.md`.
- **`@qwen3.8-flash-next-board`** — the board/hygiene session: backlog shape, decision records, hygiene
  scans, identity assets under the images directory, read-only preflight and verification from outside the
  settings forms. Tasks 2, 6, 20, 21, 23, 27.
- Unchanged: `@chatswarm` for work handed to the external research and review service, `@claire` where the
  next move is a human decision or a human-only action.

## Consequences

- A `Done` task reads as history for a named session, and a blocked task names the session that has to
  move — which is what makes a hand-off after a restart survivable.
- A model swap changes the handle text, and that is acceptable: the handle's job is to say which session
  acted, and the label now says so in words rather than by inference.
- The board session may not move implementation tasks by picking them up: if it changes repository state
  outside its scope, the assignee is wrong by construction. The scope fence is recorded on the tasks, not
  implied by the handle.
- Anything that changes `src/`, `tests/`, `SPEC.md` or the release surface carries the implementation
  handle even when the board session noticed the work.
