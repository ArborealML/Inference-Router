---
id: TASK-141
title: Include HY4 Flash in the inference router configuration
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-19T23:21:00Z'
updated_date: '2026-11-20T00:27:00Z'
labels: []
dependencies: []
priority: high
ordinal: 139000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config is amending the HY4 plan after independent review on c0a68a6. Use the engine parsed from the proposed sample to demonstrate dispatch for the loaded GLM and HY4 models and zero completion calls for an absent requested model even with positive slot data. Verify the canonical advertised model ID from source or cover an alias-only mismatch. Retain the launch-settings comparison and both config checks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config loader resolves hy4-flash and the documented selector aliases to the supported 32k profile with the observed 32768 context and one serving slot.
- [ ] #2 A focused offline check compares the configured HY4 target against the installed selector launch settings and safeguards; document any unsupported native setting or activation prerequisite.
- [ ] #3 The updated router configuration passes the config verb offline and identifies where operators enable the model.
<!-- AC:END -->
