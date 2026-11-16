---
id: TASK-10
title: Harden the backend to PAIR parity where the comparison finds gaps
status: To Do
assignee: []
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-16T20:46:00Z'
labels:
  - backend
milestone: v0.10 - live view and robustness parity
dependencies:
  - TASK-9
ordinal: 10000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Seeded by the comparison task: for every capability where our backend is weaker, a fix with acceptance criteria. Expected areas include durability across restart, crash recovery of half-finished engine switches, per-client abuse protection, and auth on the admin surface, but the list comes from evidence rather than expectation.
<!-- SECTION:DESCRIPTION:END -->
