---
id: TASK-86
title: Make board-check validate decision records, not only tasks
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:06:00Z'
labels:
  - board
dependencies: []
priority: medium
ordinal: 85000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four decision records were committed with a stray opening fence and no front matter, so the backlog CLI listed 34 of 38 decisions and every session's preflight was blind to four binding rules for about 25 minutes. tools/board-check.py passed with 0 defects because it reads only backlog/tasks/. It owns the gate that is supposed to catch exactly this.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 board-check reports a decision file with missing, unparseable or incomplete front matter (id, title, date, status) as a defect.
- [ ] #2 A CI failure results when the decision-file count and the listed decision count disagree.
- [ ] #3 The four malformed records are reproduced as a fixture, so the check is shown to fail on the real defect.
<!-- AC:END -->
