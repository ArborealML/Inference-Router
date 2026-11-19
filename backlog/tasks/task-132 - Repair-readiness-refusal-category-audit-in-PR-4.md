---
id: TASK-132
title: Repair readiness refusal-category audit in PR 4
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-19T06:15:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 130000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The build and test job on a9621f7 failed RefusalCategoryTests.SourceAuditCoversEveryFailedExit. The audit expects a SwitchReason literal on the assignment line and encountered state.FailureReason = failure.Reason. Trace each propagated reason and retain coverage for uncategorized exits. [Failed CI run](https://github.com/ArborealML/Inference-Router/actions/runs/44470429874).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The focused refusal-category tests pass on the repaired PR head and still detect an unclassified failure reason.
- [ ] #2 Hosted build and test passes on the corrected PR head.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace the readiness failure value to its producers. Fix any missing classification or adapt the audit to the proven propagation. Run the refusal-category filter and confirm hosted CI.
<!-- SECTION:PLAN:END -->
