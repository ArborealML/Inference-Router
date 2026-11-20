---
id: TASK-145
title: 'Decision: Should AIR reject long identifiers to bound admin ticket snapshots?'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-20T18:37:00Z'
labels: []
dependencies: []
ordinal: 143000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Claire decides whether a fixed byte budget for admin ticket snapshots justifies rejecting some requests that AIR accepts today. The snapshot currently returns complete identifiers for exact correlation (decision 45). The request-body limit shipped in [PR #52](https://github.com/ArborealML/Inference-Router/pull/52). Thread headers and JSON escaping also affect snapshot size. In about one minute, reply “preserve compatibility” to defer new limits, or “design limits” to commission concrete limits and a compatibility review before implementation. No command required.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record whether to preserve current compatibility or commission an identifier-limit design.
<!-- AC:END -->
