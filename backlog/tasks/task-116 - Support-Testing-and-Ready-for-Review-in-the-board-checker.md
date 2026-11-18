---
id: TASK-116
title: Support Testing and Ready for Review in the board checker
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:29:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-10
dependencies: []
modified_files:
  - tools/board-check.py
ordinal: 114000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Complete the coordinator handoff checks. A tested change may satisfy every implementation criterion before the coordinator accepts and integrates it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Both statuses count as open; misspelled statuses still fail, and Done remains excluded.
- [x] #2 The task list and working contract show both stages, their owners and the handoff requirements.
- [ ] #3 Ready for Review with all criteria checked counts as open under the coordinator. The same fixture under an implementer reports a handoff-owner defect. Preserve the existing Done evidence requirement.
- [ ] #4 The Waiting fixture rejects a missing event and an empty or placeholder wake label, and accepts a named CI completion. The self-test summary describes the cases it actually exercises.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend the existing status allowlist. Exercise each new status at the checker entry point, assert it counts as open, retain the Done and typo controls, then read back the real column order and task handoffs.

Verify each new status and a mixed Preparing, Waiting, Testing, Ready for Review and Done fixture with four open rows. Keep the malformed-status diagnostic. Read the eight-column order back using the CLI.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Contract and configured order landed in 6a02535. Checker and stage count fixtures landed in 8faba3b. Review verified the all-checked rule rejects valid review handoffs and the positive fixture retains its implementer. The Waiting rule currently recognizes a keyword alone. Add positive and negative CLI fixtures and precise output under decision-59.
<!-- SECTION:NOTES:END -->
