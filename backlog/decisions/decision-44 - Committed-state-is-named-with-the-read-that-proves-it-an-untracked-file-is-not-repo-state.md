---
id: decision-44
title: >-
  Committed state is named with the read that proves it: an untracked file is
  not repo state
date: '2026-11-18T08:36:00Z'
status: accepted
---
## Context

At 08:34Z the coding seat retracted four claims in writing after re-measuring them, and explained how it
got wrong: mid-turn it had read files that are not in `HEAD` — a version file, a canary manifest, a
release guide, a much shorter release workflow — as though they described the repository, and then found
those paths gone. This seat measured the shared worktree at 08:35Z: none of those paths exists, none has
ever been added on any ref (`git log --all --diff-filter=A` for those paths returns nothing), and the
worktree's reflog over its last eight moves contains commits only — no checkout, no reset, no stash.
Whether anything was ever present in that directory is not resolvable from the repository. What is
resolvable is that the claims described content this repository has never held, and that rows, criteria and
a hygiene question were being shaped by them within minutes.

## Decision

A claim about what is committed names the read that proves it, in the same message as the claim:
`git ls-files <path>`, `git show HEAD:<path>`, or `git log --all --diff-filter=A -- <path>`. A file that is
merely readable in a working tree says nothing about the repository: a shared worktree can hold another
session's uncommitted work, and a session's own scratch output can sit beside tracked files without
belonging to them. A report that mixes the two is retracted, not reinterpreted, and the board records
nothing from it — including nothing "corrected in place".

## Consequences

Reports carry one extra command each and get shorter. A claim that cannot name its read is phrased as an
observation about a directory, never as repository state, and no row opens on it. Retracting stays
normal and cheap: four claims were retracted in one message today and the board wrote no defect, because
the retraction arrived before the rows did. The opposite failure stays possible — a plausible report that
nobody checks — so a claim still reaches a row only after this seat reads the lines itself (decision-37),
which is how the findings against `610f0d8` got onto the board.

## Alternatives

Rely on a shared convention that agents read only tracked files (rejected: nothing enforces it, and the
whole point of a shared worktree is that several sessions' work is present at once). Have the board seat
re-verify every path in every report (rejected as a blanket rule: too slow; the rule instead makes the
reporter name the read, and this seat spot-checks what it acts on).
