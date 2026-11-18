---
id: TASK-86
title: Make board-check validate decision records, not only tasks
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T10:19:00Z'
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
- [ ] #4 Text inside an acceptance-criteria block that is not a checkbox, and a criterion repeated in the same block, are reported: TASK-82 carried both at 0 defects.
- [ ] #5 A decision record whose Context, Decision or Consequences body is empty is reported as a defect, naming the file and its byte size.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One pass over `backlog/decisions/*.md` beside the task pass: parse front matter (`id`, `title`, `date`, `status`) and the three section bodies, so a missing key, an unparsable file, or an empty section is a defect naming the file. Count decision files against what `backlog decision list` reports, so a record invisible to the CLI is also a defect. Fixtures — the four malformed records and an empty-body skeleton — join `--self-test`.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Same gate, second hole, read at 2569519: TASK-82's criteria block held three prose lines with no checkbox and the same three again verbatim — four rendered criteria where the row meant seven-ish checks, and board-check passed it with 0 defects. Fixed on the row at the same landing; the checker gap is AC #4 here.
Evidence the standard is enforced, not just written: the commit carrying a GITREF defect went red on CI (44293686450) and the repair turned it green — the board seat had piped the checker into `tail` and committed on `tail`'s exit status. The checker row itself is Done; this is its behaviour measured.

Same gate, third hole, measured at `0ddd8f3`: decisions 48–51 were 193–219 bytes — front matter and three empty headings — while the other 47 run 1.3–2.1 KB. `backlog decision list` reported all of them and the checker said 0 defects: front matter parses, nothing was said. decision-53 is the rule, AC #5 is the gate.
<!-- SECTION:NOTES:END -->
