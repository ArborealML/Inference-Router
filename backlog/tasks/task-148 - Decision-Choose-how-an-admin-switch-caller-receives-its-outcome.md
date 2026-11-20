---
id: TASK-148
title: 'Decision: Choose how an admin switch caller receives its outcome'
status: Done
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
The owner selected a queryable operation ID for an explicit admin request. Queue demand remains the normal trigger. [Decision 79](../decisions/decision-79%20-%20Return-a-queryable-ID-for-an-explicit-admin-switch.md) records the choice.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The owner selects an immediate acknowledgment with correlated result lookup or a terminal POST response, and the selected contract is recorded in a decision entry.
<!-- AC:END -->
