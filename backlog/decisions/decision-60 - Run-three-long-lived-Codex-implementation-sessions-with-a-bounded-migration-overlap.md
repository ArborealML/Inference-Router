---
id: decision-60
title: >-
  Run three long-lived Codex implementation sessions with a bounded migration
  overlap
date: '2026-11-18T18:37:00Z'
status: accepted
---
## Context

Isolated worktrees allow independent implementation without staging or source conflicts. The previous single implementation slot made the board understate concurrent work.

## Decision

The coordinator targets three long-lived Codex implementation sessions. Each owns one task and one worktree at a time. During migration, up to three existing subagents may finish alongside them, for six implementation lanes in total. A tmux worker may take independent work while a subagent finishes. The steady-state limit is three as the subagents drain.

## Consequences

Preparing, In Progress and Testing may each contain multiple tasks. The coordinator checks file overlap and serializes integration where needed. Ordinary .NET builds and focused tests can run in parallel in separate worktrees. Reserve the host under decision-55 only when the measurement specifies a host condition.
