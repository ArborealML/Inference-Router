---
id: TASK-153
title: Restore refusal mutation coverage on Pi integration
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T00:33:00Z'
updated_date: '2026-11-22T01:15:00Z'
labels: []
dependencies: []
priority: high
ordinal: 145000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Thirteen refusal-audit cases failed in the [first PR run](https://github.com/ArborealML/Inference-Router/actions/runs/44966396000) because two valid no-owner exits matched the same source anchor. The correction narrowed the anchor and all 41 focused refusal cases passed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The hosted Release suite passes the mutation audit on the integrated main revision.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The integrated refusal audit passed on main at 85e5d2e. [Merged change](https://github.com/ArborealML/Inference-Router/pull/75) and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44967781268).
<!-- SECTION:NOTES:END -->
