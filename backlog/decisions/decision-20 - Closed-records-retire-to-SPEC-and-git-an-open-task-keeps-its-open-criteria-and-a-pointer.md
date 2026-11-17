---
id: decision-20
title: >-
  Closed records retire to SPEC and git; an open task keeps its open criteria
  and a pointer
date: '2026-11-17T14:26:00Z'
status: accepted
---
## Context

The release-gate carrier task has run thirteen review rounds. Every round left a summary in its Notes and a
long criterion behind, so the file reached 2,712 words and the standard in AGENTS.md (open task readable in one
sitting: 600 words across rendered sections, no section over 300) became unreachable without deleting
information. The information is not unique: SPEC 15 and the revision log already carry each round's disposition,
and the commits carry the rest.

## Decision

**A superseded record moves; it is not copied.** A closed acceptance criterion, a finished review round's
argument, and a rejected finding live in SPEC 15 and the revision log, cited from the task by SPEC version and
sha. An open task body keeps: the next action, its **open** criteria, the current round in one or two lines, and
the pointer. Notes and Comments are a rolling window: a new entry replaces the one it supersedes. The board does
not delete a disposition it cannot see elsewhere — the SPEC row lands first (the coding session owns that file),
then the task body shrinks, in that order.

## Consequences

TASK-44 becomes short and stays open until its two genuinely open criteria close. Twelve closed criteria move to
SPEC 15 in the coding session's next landing; until that row exists they stay in the task, and the task is
allowed to be over budget while carrying them, with the debt named in its Notes. Review bookkeeping — request
ids, tiers, transcripts, payloads — stays in `.local` and is never cited in a tracked file by anything other
than the commit it produced.
