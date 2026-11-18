---
id: decision-42
title: >-
  A linked worktree shares the parent's git config: identity is supplied per
  commit, never written per worktree
date: '2026-11-18T07:28:00Z'
status: accepted
---

## Context

At 07:23Z a Codex session was given an isolated `git worktree` for delegated work. To keep its
commits off the owner's identity, `git config user.name` and `user.email` were set **inside that
worktree**. A linked worktree has its own gitdir, but `git config` without `--worktree` writes the
shared `.git/config` — worktree-scoped config needs `extensions.worktreeConfig` enabled first — so
the repository-wide identity became the Codex seat's identity for about two minutes. No commit was
made in that window, because every seat here supplies identity per commit with `-c`. The previous
value had not been read before the write and had to be restored from a terminal capture, which is
not a durable source, so the owner is asked to confirm the restored value.

## Decision

Identity is supplied per commit (`-c user.name=… -c user.email=…`) and never written into shared
configuration. Anything that must differ per worktree is passed on the command line, or confined to
`--worktree` scope with `extensions.worktreeConfig` enabled deliberately. Before any write to shared
state — repository config, hooks, environment — the current value is read and recorded in the same
act; "I put it back from what a pane showed" is not restoration.

## Consequences

One seat cannot change another's defaults by accident, and a delegated branch cannot silently inherit
or pollute an identity. Delegated branches get author and committer verified before merge, with any
mismatch corrected on the branch — unpushed, so amendable — never by rewriting published history
(decision-24). The push-range identity check (TASK-70) is the mechanical backstop.
