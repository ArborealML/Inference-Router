---
id: TASK-155
title: Use the per-user router catalog by default for serving and tuning
status: Done
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-22T01:29:00Z'
updated_date: '2026-11-22T01:35:00Z'
labels: []
dependencies: []
priority: high
ordinal: 150000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Serving, validation and tuning should read the same per-user catalog when no config is supplied.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Without an override, the three entries select the router.toml under arboreal/inference-router in the user home.
- [x] #2 Explicit config paths and supported environment overrides take precedence, and an invalid explicit path does not silently fall back.
- [x] #3 The installed CLI validates the full catalog and plans the configured tuning targets without an explicit file.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Share the default path resolution across ordinary server startup, config validation and offline tuning plan. Preserve explicit config and environment overrides. Keep tuning file validation. Update user guidance and verify from an unrelated working directory against the installed catalog.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [resolver change](https://github.com/ArborealML/Inference-Router/commit/c1524bd) and [standalone correction](https://github.com/ArborealML/Inference-Router/commit/8642bb6) passed 74 focused Release tests. The installed native executable validated the default from an unrelated directory, planned all ten selector models and fifteen aliases, honored an explicit override, and served an isolated smoke catalog from the default location.
<!-- SECTION:NOTES:END -->
