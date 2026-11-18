---
id: TASK-99
title: >-
  Bound what the router reads from an upstream error and what its keepalive
  frames repeat
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:14:00Z'
labels: []
dependencies: []
ordinal: 98000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Commit 5864695 — the board commit of 11:1xZ — landed with the machine's git identity instead of the per-commit agent identity decision-29 requires: the commit command omitted the `-c user.name` / `-c user.email` flags. It is pushed. Correcting it means rewriting pushed history, which decision-24 reserves for you. Everything after it sits on that sha.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 One line of answer: rewrite the one commit, or accept it and record the gap
- [ ] #2 Whichever way, decision-29 records the outcome, so a later seat can tell a defect from history
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Options and what changes: **rewrite** — one force-push over a commit two later commits sit on, their shas change with it, and the machine identity leaves the history; **accept** — the identity (a GitHub noreply address, no key path, no home path) stays published, and the guard becomes mechanical: a CI check that refuses a commit whose author is not a configured agent identity, which the board will open as its own row. Blocked on @claire because decision-24 forbids rewriting pushed history without your word. Cost to answer: two minutes. This row does not sit in the same cluster as the other three: it is the only one where a pushed artifact is involved.
<!-- SECTION:NOTES:END -->
