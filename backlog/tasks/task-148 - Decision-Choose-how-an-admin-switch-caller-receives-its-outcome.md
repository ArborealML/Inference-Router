---
id: TASK-148
title: 'Decision: Choose how an admin switch caller receives its outcome'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-20T21:26:00Z'
labels: []
milestone: m-7
dependencies: []
ordinal: 146000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Review the current admin switch contract in [SPEC §5.5](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md). An accepted request returns 202 before the load finishes; the CLI exits on that acknowledgment. Reply A or B in the existing decision thread (about one minute, no command required). A returns an operation ID immediately and adds an exact result lookup. B holds the POST open through load and returns its terminal outcome on that connection. This selects the caller contract for TASK-104.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The owner selects an immediate acknowledgment with correlated result lookup or a terminal POST response, and the selected contract is recorded in a decision entry.
<!-- AC:END -->
