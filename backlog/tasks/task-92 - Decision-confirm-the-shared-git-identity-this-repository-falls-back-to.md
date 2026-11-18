---
id: TASK-92
title: 'Decision: confirm the shared git identity this repository falls back to'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T07:57:00Z'
labels:
  - identity
dependencies: []
priority: medium
ordinal: 91000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
At 07:23Z a config write made from a delegated worktree overwrote this repository's shared git identity for about two minutes: a linked worktree shares the parent repository config, so the write was repository-wide (decision-42). No commit landed in the window, because every seat here supplies its identity per commit. The previous value had not been read before the write, so it was restored from a terminal capture — author name shadowndacorner with that account's noreply address — which is a guess with a timestamp, not a verified restoration. The answer needed is one line: is that the identity you want the repository to fall back to, or should no shared identity be set at all? Cost: about two minutes, one command. What it unblocks: nothing is waiting on it. It closes the last loose end of the identity work (decision-29, decision-42) so the push-range author-and-committer guard can name the fallback honestly instead of assuming one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The owner states, in one line, either the shared identity the repository should carry, or that no shared fallback is wanted and per-commit identity is the only route.
<!-- AC:END -->
