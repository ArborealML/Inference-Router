---
id: TASK-47
title: Trim the Description bodies of the open tasks to 300 words
status: Done
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T11:03:00Z'
updated_date: '2026-11-17T12:20:00Z'
labels: []
dependencies: []
priority: medium
type: chore
ordinal: 46000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Closed: the open set over 300 words is now empty.** The set moves every time a Description is trimmed, so
re-run the checker rather than trusting any list written here — as I last measured it the open set was TASK-2 (627),
6 (341), 11 (371), 14 (513), 23 (353), and all five are now under the line. Nine Done tasks (TASK-7, 10, 20, 24, 27,
36, 37, 40, 41) are over it and are exempt history under decision-18; their bodies are not rewritten.

**The method that worked, for the next time:** re-read the file; cut whole trailing paragraphs out of
`## Description` and append them to `## Implementation Notes` with `--append-notes` — nothing deleted, nothing
rewritten; leave the answer or wake line, the steps, the cost and the links. `--description` is a total overwrite and
`--acceptance-criteria` replaces the whole AC list (repeat the flag once per criterion in one call). Re-read the
file after every write.

    python3 the board's untracked lint script        # 0 defects, exit 0
    python3 the board's untracked standards script   # 0 defects, exit 0; WORDS fires for non-Done tasks only

**What must survive a shrink** — the checkers test the same things, so a trim that breaks one shows up as a defect
rather than a quieter task: an answer or wake line, at least one backticked command or path, a stated cost, links to
files rather than folders, and the wake or blocker sentence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 backlog task list is unchanged in shape: no task loses an answer line, a command, a cost, a link or an acceptance criterion while its Description shrinks
- [x] #2 moved prose lands in Implementation Notes or a task comment, not deleted
- [x] #3 python3 the board's untracked standards script reports 0 WORDS defects for every non-Done task
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T12:20:00Z

---

Closing counts, measured by the board session on this tree, not inherited: `python3 the board's untracked lint script` -> 0 defects, exit 0. `python3 the board's untracked standards script` -> 0 defects, exit 0, plus its informational line: 9 Done tasks over 300 words (TASK-7, 10, 20, 24, 27, 36, 37, 40, 41), history under decision-18. Before this pass the same command reported 6 defects exit 1 (5 WORDS on open tasks plus 1 ORDINAL collision between TASK-23 and TASK-29 at 23000). Five Descriptions were shortened by moving whole paragraphs to Implementation Notes — TASK-2 to 281 words (357 moved), TASK-6 to 256 (96), TASK-11 to 200 (182), TASK-14 to 283 (241), TASK-23 to 258 (106) — counts are the checker's own, as-of this commit.--plain

---

created: 2026-11-17T12:20:00Z

---

The trims landed in 2c31eb4 (board: TASK-47 done — every open Description is under 300 words), pushed to main. Prior board commits in the same pass: 564ee7b (ordinal collision), ecd1f19 (ninth review on TASK-44), 24e3058 (TASK-17/18 history comments).

---
<!-- COMMENTS:END -->
