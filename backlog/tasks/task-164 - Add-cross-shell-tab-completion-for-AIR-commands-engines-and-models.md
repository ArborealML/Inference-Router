---
id: TASK-164
title: Add cross-shell tab completion for AIR commands, engines and models
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:14:00Z'
updated_date: '2026-11-22T03:47:00Z'
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
- [x] #1 Tab completes supported commands, options, engine names and model IDs or aliases in the argument positions that accept them.
- [x] #2 Engine and model candidates come from the configured catalog, including cold models, without triggering inference or a model switch.
- [x] #3 Document how to enable completion and verify completion in real Bash and PowerShell sessions, including quoting and an unavailable listener.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse one candidate source across shell adapters. Check supported shells and conventions, implement Bash and PowerShell registration, then exercise the installed air command in both shells.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Verified native completion in Bash, PowerShell, Zsh and fish. The applied catalog takes precedence for client verbs, including cold choices. Offline tuning reads its selected file. Reviewed server selection uses an explicit URL first, then AIR_ROUTER_URL, then loopback. Home-relative paths retain their shell spelling. [Merged implementation](https://github.com/ArborealML/Inference-Router/pull/78).
<!-- SECTION:NOTES:END -->
