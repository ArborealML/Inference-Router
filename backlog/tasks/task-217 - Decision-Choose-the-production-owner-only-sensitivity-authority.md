---
id: TASK-217
title: "Decision: Choose the production owner-only sensitivity authority"
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 217000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner selects independently authorized principal/root scope and, separately, the external human-only lowering privilege or disabled lowering. Agent isolation, authentication and credential issuance belong outside AIR.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record independently authorized principal/root scope and permitted independent-root creation, or deny it; AIR verifies the supplied scope.
- [ ] #2 Record separately enabled human-only lowering privilege with externally established credential isolation, or disabled lowering; AIR verifies scope and policy revision.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review TASK-208's proposed privilege contract and external deployment prerequisite. Reply with selected existing control/credential mechanism or disabled; about five minutes. No credentials or sandbox implementation in AIR. Denied root scope cannot authorize commercial operation. Disabled lowering alone does not block an otherwise authorized fixed insensitive root; no lowering claim without actual external evidence.
<!-- SECTION:PLAN:END -->
