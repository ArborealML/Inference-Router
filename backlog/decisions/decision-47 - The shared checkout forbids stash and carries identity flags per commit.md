---
id: decision-47
title: >-
  The shared checkout forbids stash and carries identity flags per commit
date: '2026-11-18T11:20:00Z'
---

# Decision: The shared checkout forbids stash and carries identity flags per commit

## Context

Two seats work in one checkout. A seat that stashes to measure a clean tree sweeps the other seat's
uncommitted work out from under it: the coding seat ran `git stash -u` to re-measure a clean HEAD
while the board seat had uncommitted rows in the same directory. Nothing was lost, and the near-miss
was avoidable by construction. The same turn exposed a second mechanical failure: a `board:` commit
was made without the per-commit identity flags of decision-29 and landed with the machine identity,
already pushed.

## Decision

In the shared checkout: **no `git stash`** — park work in progress under the gitignored `.local/wip`
and measure a clean tree in a separate worktree or by checking out the commit elsewhere. And **identity
flags go in the command**, `git -c user.name=… -c user.email=… commit …`, never in shared config
(decision-29, decision-42): a seat that cannot show the flags in its command did not follow the rule.
A commit that lands with the wrong identity is reported, tracked as its own row and answered by the
owner; it is not repaired by rewriting pushed history (decision-24).

## Rationale

Stash is a whole-tree operation in a tree that is not whole to one owner, and its failure mode is
silent: the files simply reappear, or do not. Identity flags in shared config would leak one seat's
identity into the other's commits, which is the failure decision-42 already ruled out. Making both
rules mechanical — a parking directory and flags in the command — removes the two ways a careful
seat still gets this wrong.

## Consequences

Clean-tree measurements cost a worktree instead of a stash, which is slower and auditable. A wrong
identity in pushed history is a permanent artifact that has to be accepted or rewritten by the owner,
and the durable fix is a CI check on commit authorship, which the board opens as its own row once the
owner's answer lands.
