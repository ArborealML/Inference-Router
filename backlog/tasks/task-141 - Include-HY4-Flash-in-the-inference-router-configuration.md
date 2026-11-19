---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: To Do
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
labels: []
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add the installed selector’s HY4 Flash Maya-XS entry to the maintained router configuration. Config owns this companion after handing off the selected DeepSeek and Qwen pair plan. Preserve the first-swap pair and historical selector census.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config loader resolves hy4-flash and the documented selector aliases to the supported 32k profile with the observed 32768 context and one serving slot.
- [ ] #2 A focused offline check compares the configured HY4 target against the installed selector launch settings and safeguards; document any unsupported native setting or activation prerequisite.
- [ ] #3 The updated router configuration passes the config verb offline and identifies where operators enable the model.
<!-- AC:END -->
