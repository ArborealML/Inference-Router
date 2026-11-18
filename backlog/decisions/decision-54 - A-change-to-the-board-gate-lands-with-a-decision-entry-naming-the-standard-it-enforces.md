---
id: decision-54
title: >-
  A change to the board gate lands with a decision entry naming the standard it
  enforces
date: '2026-11-18T15:29:00Z'
status: accepted
---
## Context

`tools/board-check.py` decides what CI refuses, so changing the script changes what the board is allowed to
be. The row that added the unpublished-path citation rule went red on CI (44293686450) for exactly this
reason: once a gate starts reporting something, the standard behind it is invisible unless somebody wrote it
down. Two open rows do the same again — decision-record validation and empty section bodies on TASK-86,
marker well-formedness on TASK-106 — and nothing in the contract says the commit that lands a check also
records why the check is a standard.

## Decision

A commit that changes **what the gate reports** — a new defect class, or a widened or narrowed one — lands
with a decision entry naming the standard it enforces and the defect that motivated it, in the same commit.
Code without the record is a gate change nobody agreed to.

## Consequences

The checker's vocabulary is auditable from `backlog/decisions/` rather than by reading the script. The board
seat owns both halves — the dispatch and the record; the implementer seat owns the `tools:` commit that
carries them. A reviewer who thinks a check is wrong now has something to argue with, and a check that
reportedly fires on nothing is prevention rather than repair, which the row has to say out loud.
