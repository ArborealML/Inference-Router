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

A change to **what the gate reports** — a new defect class, or a widened or narrowed one — requires a decision
entry naming the standard it enforces and the defect that motivated it. The seat split decides the mechanism:
the record is written by the board seat under `backlog/decisions/`, the code by the implementer seat under
`tools:`, so one commit cannot hold both. The record lands in the same push, and the gate-change commit cites
the decision id; code without a record to cite is a gate change nobody agreed to. A change that only conforms
to a standard already on the books cites that standard instead of opening a new record.

## Consequences

The checker's vocabulary is auditable from `backlog/decisions/` rather than by reading the script. The board
seat owns the record and the dispatch; the implementer seat owns the `tools:` commit that cites it, and the
citation is what makes the rule checkable across the split rather than a courtesy. A reviewer who thinks a
check is wrong now has something to argue with, and a check that reportedly fires on nothing is prevention
rather than repair, which the row has to say out loud.
