---
id: TASK-106
title: Make board-check report a malformed section marker
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T13:27:00Z'
labels: []
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
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by the board while editing rows in this tree: `grep -rn "^SECTION:" backlog/tasks/` listed four files at the time of writing, all closing markers; the board repaired those files in the same commit. Cheap rule, worth having before the corpus outgrows anyone's ability to eyeball it. Sequencing decided by the board: it lands after the stop-cause change, because TASK-87 is the gate and nothing waits on this row.
<!-- SECTION:NOTES:END -->
