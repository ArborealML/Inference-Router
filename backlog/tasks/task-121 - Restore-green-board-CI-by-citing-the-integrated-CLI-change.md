---
id: TASK-121
title: Restore green board CI by citing the integrated CLI change
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T20:27:00Z'
updated_date: '2026-11-18T20:30:00Z'
labels: []
milestone: m-1
dependencies: []
priority: high
ordinal: 119000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The [failed run](https://github.com/ArborealML/Inference-Router/actions/runs/44390448761) at 74f4ce3 rejected a worker-only commit cited by the completed CLI task. The published integration 2764b6a now identifies that change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The completed CLI task cites its published main commit and the replacement board-contract CI job passes.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [replacement board-contract job](https://github.com/ArborealML/Inference-Router/actions/runs/44391298439/job/135109364112) passed on f09a81b after a fresh checkout.
<!-- SECTION:NOTES:END -->
