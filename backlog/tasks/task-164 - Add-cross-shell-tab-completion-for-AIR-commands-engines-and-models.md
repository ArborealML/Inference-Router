---
id: TASK-164
title: Add cross-shell tab completion for AIR commands, engines and models
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:14:00Z'
updated_date: '2026-11-22T03:37:00Z'
labels: []
dependencies: []
priority: low
ordinal: 159000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Last in the current queue, after the five active follow-ups. Provide shell-native tab completion for air, including Bash and PowerShell. Inventory existing shell integration before adding adapters for other supported shells.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tab completes supported commands, options, engine names and model IDs or aliases in the argument positions that accept them.
- [ ] #2 Engine and model candidates come from the configured catalog, including cold models, without triggering inference or a model switch.
- [ ] #3 Document how to enable completion and verify completion in real Bash and PowerShell sessions, including quoting and an unavailable listener.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse one candidate source across shell adapters. Check supported shells and conventions, implement Bash and PowerShell registration, then exercise the installed air command in both shells.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Native Bash, PowerShell, Zsh and fish registration and candidate insertion were exercised, including cold catalog choices and quoting. The installed CLI resolved ten engines. Final review checks live-catalog precedence over stale disk and home-relative paths.
<!-- SECTION:NOTES:END -->
