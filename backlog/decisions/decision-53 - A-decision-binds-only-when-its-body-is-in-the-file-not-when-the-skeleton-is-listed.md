---
id: decision-53
title: >-
  A decision binds only when its body is in the file, not when the skeleton is
  listed
date: '2026-11-18T15:23:00Z'
status: accepted
---
## Context

Decisions 48 through 51 — four binding rules about the switch transaction — were created through the CLI and
committed. `backlog decision list` reported all 51 records and board-check reported 0 defects, yet those four
files were 193–219 bytes: front matter, three empty headings, no text, while every other decision file runs
1.3–2.1 KB. Measured at `0ddd8f3` with `wc -c backlog/decisions/*`. `backlog decision edit` writes only the
status line, so nothing in the toolchain ever wrote a body. The rules existed only in this seat's notes and
in the task rows that cite them by number.

## Decision

A decision binds when both halves exist: the CLI lists it **and** its Context, Decision and Consequences text
is in the file. After creating one, write the body in the same act, then re-read the file (`wc -c`, or
`git show <sha>:<path>`) and re-run the decision list before committing. A ~200-byte record is a defect, not
a stub.

## Consequences

decision-39's list check is necessary and not sufficient: it proves the front matter parses, not that anything
was said. The board checker has to report a decision record whose section bodies are empty — that criterion is
on TASK-86. A rule quoted in a task row, a review or a commit message is not a decision until its body is in
`backlog/decisions/`.
