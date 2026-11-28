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
Owner chooses the external control-layer privilege contract that AIR will consume for owner-authorized root creation/de-escalation, or keeps those privileged operations disabled. Agent isolation, authentication and credential issuance belong outside AIR.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record chosen external privilege/scope contract and who may authorize independent roots or de-escalation; AIR verifies the supplied scope and policy revision.
- [ ] #2 Record affirmative approved deployment boundary or keep privileged operations disabled; do not claim human-only behavior without externally established credential isolation.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review TASK-208's proposed privilege contract and external deployment prerequisite. Reply with selected existing control/credential mechanism or disabled; about five minutes. No credentials or sandbox implementation in AIR. A refusal does not authorize commercial operation requiring this boundary.
<!-- SECTION:PLAN:END -->
