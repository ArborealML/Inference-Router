---
id: TASK-116
title: Support Testing and Ready for Review in the board checker
status: Done
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:29:00Z'
updated_date: '2026-11-18T18:36:00Z'
labels: []
dependencies: []
modified_files:
  - tools/board-check.py
ordinal: 114000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Testing and Ready for Review are supported in the board and checker. Sol delivered the tested diff to the coordinator.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Both statuses count as open; misspelled statuses still fail, and Done remains excluded.
- [x] #2 The task list and working contract show both stages, their owners and the handoff requirements.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend the existing status allowlist. Exercise each new status at the checker entry point, assert it counts as open, retain the Done and typo controls, then read back the real column order and task handoffs.

Verify each new status and a mixed Preparing, Waiting, Testing, Ready for Review and Done fixture with four open rows. Keep the malformed-status diagnostic. Read the eight-column order back using the CLI.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Contract and configured order landed in 6a02535. Checker and regression fixtures landed in 8faba3b. CLI and checker readbacks verified delivery, open counts and the named wake requirement.
<!-- SECTION:NOTES:END -->
