---
id: TASK-121
title: Restore green board CI by citing the integrated CLI change
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T20:27:00Z'
updated_date: '2026-11-18T20:27:00Z'
labels: []
milestone: m-1
dependencies: []
priority: high
ordinal: 119000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on replacement board-contract CI. The [failed run](https://github.com/ArborealML/Inference-Router/actions/runs/44390448761) at 74f4ce3 rejected a worker-only commit cited by the completed CLI task. The board now cites the published integration 2764b6a.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The completed CLI task cites its published main commit and the replacement board-contract CI job passes.
<!-- AC:END -->
