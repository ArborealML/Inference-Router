---
id: TASK-217
title: "Decision Choose the production owner-only sensitivity authority"
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
Owner action when the policy integration is ready: select the trusted confirmation boundary for lowering sensitivity. Choose a separate OS/service privilege with an owner-only credential, or an equivalent protected human approval surface. Agent-accessible credentials cannot satisfy either option.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record one chosen boundary, owner identity and who can mint new insensitive roots; state how agent shell/process access is excluded.
- [ ] #2 Authorize the chosen production configuration or keep external routing disabled until the boundary is available.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review the TASK-208 threat model and proposed operational configuration. Reply with the chosen option or a constraint; about five minutes. No credentials are placed in the board.
<!-- SECTION:PLAN:END -->
