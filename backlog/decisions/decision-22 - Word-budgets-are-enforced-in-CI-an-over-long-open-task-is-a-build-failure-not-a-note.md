---
id: decision-22
title: >-
  Word budgets are enforced in CI: an over-long open task is a build failure,
  not a note
date: '2026-11-17T16:52:00Z'
status: accepted
---
## Context



## Decision



## Consequences

## Context

The board's word limits came out of the owner's complaint that tasks had become unreadable, and for a day they were
advisory: `tools/board-check.py` printed `BUDGET` notes and the CI board job stayed green. Advisory limits measured
nothing that could stop the corpus growing again — the numbers went 19,738 → 9,693 words across twenty open tasks only
because one session pushed them down, and a limit that lives in prose is a preference with a number attached.

## Decision

Budgets are a gate. The CI `board contract` job runs `python3 tools/board-check.py --strict-budget`, so an open task over
**600 words in its whole body** or **300 words in any one section** fails the build. The evidence at the moment the gate
closed (commit 5b05ab7, measured in a fresh clone of `origin/main` at 2a68e4c): **0 defects, 48 task files, 20 open tasks,
9,693 words, median 529, largest 596, note list empty, `--strict-budget` exits 0**.

## Consequences

A task that grows past the limit is trimmed or split in the same commit that grew it — the limit does not move down to fit
the work, and nobody disables the flag to land a change. Trimming has a required destination: superseded prose goes to the
tracked archive file or a SPEC row before it leaves the task, and standing rules move into Description, a criterion, Notes
or SPEC first. Two remedies stay legitimate and are not defects: splitting a task that is really two tasks, and closing
work early. One is not: deleting a record without a home.
