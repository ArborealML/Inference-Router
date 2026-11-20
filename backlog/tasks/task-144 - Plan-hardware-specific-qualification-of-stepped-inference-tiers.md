---
id: TASK-144
title: Plan hardware-specific qualification of stepped inference tiers
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-20T00:33:00Z'
labels: []
dependencies: []
ordinal: 142000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Explore a reusable hardware tuning framework for stepped context tiers across models. Start from the HY4 ladder and preserve supported model-specific profiles and defaults. Separate configured targets, unsupported combinations and measured capacity for a particular host, model build and runtime configuration. Keep this future work independent of the first native swap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Propose a repeatable hardware-specific sweep recording model, runtime, GPU allocation, memory limits, requested and observed context, concurrency, and success or failure.
- [ ] #2 Define how measured results inform engine TOML and profile selection, including safe handling of failed or untested tiers and preservation of existing model-specific defaults.
<!-- AC:END -->
