---
id: decision-39
title: >-
  A decision binds only once the CLI lists it: create records through the CLI
  and re-run the list
date: '2026-11-18T06:07:00Z'
status: accepted
---
## Context

Commit `e03328d` recorded four decisions — 33 through 36, the switch publication-authority rules —
as files under `backlog/decisions/`. Each opened with a bare `---` and carried no front matter, so
the CLI parsed no record: the decision list reported 34 of 38 decisions and the four were absent
from every other session's preflight for about 25 minutes, while the board checker reported 0 defects
because it reads only `backlog/tasks/`. The rules read as binding and were invisible.

## Decision

A decision exists when the tool lists it. Create records with the CLI rather than by writing a file,
and after any commit that adds or edits one, run the decision list and confirm the ids appear — the
same check already required after a task edit. A board checker has to cover the record kinds the
contract treats as binding; closing that coverage gap is the checker owner's act, not a hand edit in
someone else's path.

## Consequences

Board writers verify through the CLI, not by re-reading their own file. The decision count becomes a
number worth comparing at every landing, and a malformed record becomes a build failure once the
checker covers it.
