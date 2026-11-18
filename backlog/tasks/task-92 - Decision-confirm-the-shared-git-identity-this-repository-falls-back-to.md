---
id: TASK-92
title: 'Decision: confirm the shared git identity this repository falls back to'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-18T07:57:00Z'
updated_date: '2026-11-18T18:44:00Z'
labels:
  - identity
dependencies: []
priority: medium
ordinal: 91000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner confirmed shadowndacorner, the account used by the default SSH credentials, as the shared fallback. The configured Git user.name matched at readback. Per-commit identities remain required for agent work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The owner states, in one line, either the shared identity the repository should carry, or that no shared fallback is wanted and per-commit identity is the only route.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The owner ruling is recorded in [decision-29](https://github.com/ArborealML/Inference-Router/blob/main/backlog/decisions/decision-29%20-%20Each-seat-commits-as-a-named-agent-identity-supplied-per-commit-not-from-shared-config.md).

Repository-local and effective user.name and user.email were read back. Both resolve to the confirmed account and its GitHub noreply address.
<!-- SECTION:NOTES:END -->
