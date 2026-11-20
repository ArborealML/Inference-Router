---
id: TASK-142
title: Validate an enrolled two-engine topology for the first native Pi swap
status: Done
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-20T00:12:00Z'
updated_date: '2026-11-20T04:18:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 140000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The internal enrolled-pair verifier validates immutable configuration, route and member identities against current evidence before returning a closed descriptor. Public startup and live serving still require host enrollment.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The selected two-engine configuration can pass topology validation with exact verified enrollment covering both engines and the shared host partition.
- [x] #2 The existing dedicated-engine guard still refuses an unverified, partial, mismatched or stale enrollment before lifecycle mutation.
- [x] #3 Focused offline controls distinguish topology validation from launch authority, readiness and permission to forward.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review of the plan on 0b161d3 requires coverage of the public DTO conversion, router construction and engine startup guards. Amend the internal verifier to compare frozen configuration and target inputs with an independently current revision and digest. Reject old evidence replayed in the same generation. Require authority-issued membership for both exact engines, devices and routes. Exercise those checks offline; public startup continues to refuse the shared topology until a real enrollment producer supplies the evidence.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed in [PR #40](https://github.com/ArborealML/Inference-Router/pull/40). The reviewed source passed 17 focused cases and two independent comparison-deletion controls. The SPEC-only reconciliation preserved the qualified code; hosted CI passed.
<!-- SECTION:NOTES:END -->
