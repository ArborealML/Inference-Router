---
id: TASK-209
title: "Integrate owned sensitivity and restrictions into Pi"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-184
  - TASK-207
  - TASK-208
priority: high
ordinal: 209000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pi remains a client of AIR, not a router. Preserve session controls, captured turns, compaction and restoration while adding visible sensitivity, escalation and owner-directed declassification through the selected trusted surface.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ordinary and auxiliary calls, forks, compaction and restored sessions carry owned lineage and effective restrictions; direct commercial choice cannot bypass the supported AIR integration.
- [ ] #2 Escalation tool waits for durable acknowledgment before dependent work; failed escalation blocks that work.
- [ ] #3 User sees sensitivity and affected transcript scope; the agent cannot approve declassification.
- [ ] #4 Eligibility-filtered catalog drives context ceiling; lower eligibility warns/refuses or compacts privately.
- [ ] #5 Pinned Pi version and final outbound hook tests cover both paths, retries and restored sessions. Installed bytes match reviewed source before live release.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse session identity and catalog helpers, bind scoped credentials; do not introduce a client route scorer. Test with capture fixtures, then reconcile installed copy under TASK-188. Document unsupported arbitrary tool/shell egress.
<!-- SECTION:PLAN:END -->
