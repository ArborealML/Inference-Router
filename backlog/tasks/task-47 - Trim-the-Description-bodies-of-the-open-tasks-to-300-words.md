---
id: TASK-47
title: Trim the Description bodies of the open tasks to 300 words
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T11:03:00Z'
updated_date: '2026-11-17T11:39:00Z'
labels: []
dependencies: []
priority: medium
type: chore
ordinal: 46000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (about an hour, no code, no risk).** Nine open task Descriptions are over the 300-word standard; trim
them by moving reasoning into Implementation Notes, deleting nothing. **The set moves every time a Description is
trimmed, so re-run the checker rather than trusting the list below** — as I last measured it the open set is TASK-11, 14, 23, 28 —
TASK-32, 33, 38 and 42 were trimmed while this task sat open; Done tasks are exempt history and keep their length on purpose.

    python3 .local/board-standards.py      # counts Defects by check; WORDS is the only open class
    python3 .local/board-lint.py           # must stay at 0 defects, both before and after

**Method, one task at a time.** Re-read the file. Cut the reasoning paragraphs out of `## Description` and append
them to `## Implementation Notes` with `--append-notes`. Leave in the Description the answer or wake line, the
do-this steps, the cost in minutes, and the links. Re-read the file after every write: `--description` is a total
overwrite, `--acceptance-criteria` replaces the whole AC list (repeat the flag once per criterion, in one call),
and the AC block is a separate managed region.

**What must survive the shrink** — the same linters check the same things, so a trim that breaks one shows up as a
new defect rather than a quieter task: an answer or wake line inside the first 30 lines, at least one backticked
command or path, a stated cost, a link to a file rather than a folder, and the wake or blocker sentence.

**Done when** `board-standards.py` reports 0 WORDS defects for every non-Done task and `board-lint.py` still
reports 0 defects with exit 0. Commit as `board:` and stay inside `backlog/`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 backlog task list is unchanged in shape: no task loses an answer line, a command, a cost, a link or an acceptance criterion while its Description shrinks
- [ ] #2 moved prose lands in Implementation Notes or a task comment, not deleted
- [ ] #3 python3 .local/board-standards.py reports 0 WORDS defects for every non-Done task
<!-- AC:END -->
