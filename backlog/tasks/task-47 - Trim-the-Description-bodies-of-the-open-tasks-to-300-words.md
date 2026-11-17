---
id: TASK-47
title: Trim the Description bodies of the open tasks to 300 words
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-17T11:03:00Z'
labels: []
dependencies: []
priority: medium
type: chore
ordinal: 46000
---

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 backlog task list is unchanged in shape: no task loses an answer line, a command, a cost, a link or an acceptance criterion while its Description shrinks
- [ ] #2 moved prose lands in Implementation Notes or a task comment, not deleted
- [ ] #3 python3 .local/board-standards.py reports 0 WORDS defects for every non-Done task
<!-- AC:END -->
