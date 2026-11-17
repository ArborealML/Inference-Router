---
id: decision-19
title: >-
  Open-task bodies carry the decision, not the argument: 200 words of
  Description, 250 words of the rest
date: '2026-11-17T14:11:00Z'
status: accepted
---

## Context
Descriptions sit under the 300-word line and the checker reports zero defects, while the files a human opens keep growing: 21 open task files, 22,624 words, median about 1,100, worst 2,758 (TASK-44), and 9 of 21 Descriptions sit between 250 and 299 words. decision-15 and decision-18 moved reasoning out of Descriptions; it landed in `Implementation Notes` and `Comments` instead, so the file got longer while the defect count stayed clean. A count that cannot see the complaint is the wrong count.

## Decision
**For an open task: Description at most 200 words, and all non-Description body content — criteria, notes, plan, comments — at most 250 words.** Criterion text counts, because criteria are the contract. Review dispositions live where they already live: SPEC 15 for the argument, `.local` for the bookkeeping; a task carries the one sentence that points there, plus the sha. Done task bodies stay history (decision-18) and are reported as an informational count, never as a defect.

## Consequences
Measured at b214ce6, **20 of the 21 open tasks are already over one of the two lines**, so this is a remediation pass and not a style preference: TASK-44 hardest (2,758 words), then TASK-29, TASK-28, TASK-21, TASK-2, TASK-14. Trimming is safe only if the argument survives somewhere durable, so each cut line must either already exist in SPEC 15 or be moved there in the same landing — the board does not delete a disposition, it relocates it. The checker gains both counts and prints open defects separately from informational counts; until it does, the board quotes the file-word profile by hand rather than claiming a pass.
