---
id: TASK-115
title: Recognize active preparation in the backlog status check
status: Done
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:24:00Z'
updated_date: '2026-11-18T18:28:00Z'
labels: []
dependencies: []
ordinal: 113000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Preparing is available in the backlog and recognized by the checker. The inventory task rendered there during active investigation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Preparing is accepted by the board checker and an unknown status still raises STATUS.
- [x] #2 The configured column order and working contract include Preparing, and the active inventory task renders there.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Add Preparing to the existing checker allowlist. Exercise a valid Preparing row and an invalid spelling through the checker entry point. Read the configured order and task status back with the CLI and run the strict corpus check.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Column and contract landed in 40e9395, checker and fixtures in ab99552. CLI readback confirmed the configured order. The self-test accepts Preparing, rejects Prepairing and excludes Done from the open count.
<!-- SECTION:NOTES:END -->
