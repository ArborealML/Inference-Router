---
id: TASK-142
title: Validate an enrolled two-engine topology for the first native Pi swap
status: Preparing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-20T00:12:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 140000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Shutdown is tracing the dedicated-engine refusal for the selected DeepSeek and Qwen pair. Define the smallest configuration and startup path admitting only a verified shared enrollment. Preserve refusal for unknown, partial or mismatched enrollment, including on restart.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The selected two-engine configuration can pass topology validation with exact verified enrollment covering both engines and the shared host partition.
- [ ] #2 The existing dedicated-engine guard still refuses an unverified, partial, mismatched or stale enrollment before lifecycle mutation.
- [ ] #3 Focused offline controls distinguish topology validation from launch authority, readiness and permission to forward.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace configuration loading and runtime construction at the landed source, identify the owner of the trusted enrollment, and map dependencies on the receipt and serving paths. Return a bounded plan for independent review; keep the live host untouched.
<!-- SECTION:PLAN:END -->
