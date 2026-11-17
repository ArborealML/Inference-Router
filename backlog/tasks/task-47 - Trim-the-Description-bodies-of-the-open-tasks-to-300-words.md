---
id: TASK-47
title: Trim the Description bodies of the open tasks to 300 words
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T11:03:00Z'
updated_date: '2026-11-17T11:07:00Z'
labels: []
dependencies: []
priority: medium
type: chore
ordinal: 46000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Do this (about an hour, no code, no risk).** The owner standard is that a Description is human readable and not verbose; the lint puts the line at 300 words and 17 task files are over it. Nine of those are open tasks, so they are the ones a reader actually opens: TASK-2, 6, 11, 23, 24, 28, 33, 41, 42. The rest are Done history and stay as they are.

**Method, in order, one task at a time.** Re-read the file, then move the reasoning paragraphs out of `## Description` into `## Implementation Notes` — never delete them — leaving the answer line, the do-this steps, the cost, and the links. Re-read the file after each `backlog task edit`, because a `--description` overwrite is total and the AC block is a separate managed region: `backlog task edit TASK-11 --description $'...'` and nothing else in the same call.

    python3 .local/board-standards.py      # 17 WORDS defects today
    python3 .local/board-lint.py           # must stay at 0 defects

**What must not be lost while shrinking:** the answer line in the first 30 lines, at least one backticked command or path, a stated cost in minutes, a link to a file rather than a folder, and any wake or blocker sentence — the same lint checks the same things, so a trim that breaks one of those shows up as a new defect rather than as a quieter task.

**Done when** `board-standards.py` reports 0 WORDS defects for every non-Done task and `board-lint.py` still reports 0 defects. Commit as `board:` and do not touch anything outside `backlog/`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 backlog task list is unchanged in shape: no task loses an answer line, a command, a cost, a link or an acceptance criterion while its Description shrinks
- [ ] #2 moved prose lands in Implementation Notes or a task comment, not deleted
- [ ] #3 python3 .local/board-standards.py reports 0 WORDS defects for every non-Done task
<!-- AC:END -->
