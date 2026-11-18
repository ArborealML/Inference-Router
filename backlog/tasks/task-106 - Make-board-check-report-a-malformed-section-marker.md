---
id: TASK-106
title: Make board-check report a malformed section marker
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T13:27:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels: []
milestone: m-12
dependencies: []
ordinal: 104000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The checker reads section bodies between `SECTION:<NAME>:BEGIN` and `:END` markers, so a closing marker that lost its comment opener — a line reading `SECTION:NOTES:END -->` — still parses, and four task files carried one silently. A row whose section runs past its intended end, or whose marker shows as prose in the browser, is a formatting defect the gate should name rather than something a human finds by eye while reading a card.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 A task file whose `SECTION:` or `AC:` marker line is not a well-formed HTML comment is reported as a defect, naming file and line
- [ ] #2 A fixture for that case joins the checker's own tests
- [ ] #3 A marker misspelled out of the known set is reported too, so a renamed section is not silently dropped from the render
- [ ] #4 Body text stranded outside a known section's markers — a missing opener leaving a duplicate under the heading — is reported with the heading
- [ ] #5 A marker quoted in prose or a fence is **not** counted as a structural marker, and a fixture carries both cases
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Markers are matched only in comment form today: `tools/board-check.py:157` at `0ddd8f3` searches for the full comment form of the `AC:BEGIN` pair, so a line reading `SECTION:NOTES:END -->` — the opener lost — is no marker at all and the section body runs on unnoticed. Scan every line holding `SECTION:` or `AC:BEGIN`/`AC:END` and require the complete comment, reporting file, line and marker name; report a name outside the known set (AC #3); report text stranded outside markers under a rendered heading (AC #4). Fixtures join `--self-test`, which already runs the checker over temporary task files and asserts its exit code and defect names.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by the board while editing rows in this tree: `grep -rn "^SECTION:" backlog/tasks/` listed four files at the time of writing, all closing markers; the board repaired those files in the same commit. Cheap rule, worth having before the corpus outgrows anyone's ability to eyeball it. Sequencing decided by the board: it lands after the stop-cause change, because TASK-87 is the gate and nothing waits on this row — and a check that reports nothing in the current corpus is prevention, not repair. TASK-86 #5 is the sibling hole in the same file (empty decision bodies); different deliverable, separate commit.

AC #5 came from this row's own edit, measured: the marker count is taken over the whole body, so prose that quotes a marker in full comment form is counted as one and the row came back `ACBLOCK`. The check has to tell a marker from a quotation of one, or it pushes writers into paraphrase.
<!-- SECTION:NOTES:END -->
