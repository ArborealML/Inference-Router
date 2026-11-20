---
id: TASK-142
title: Validate an enrolled two-engine topology for the first native Pi swap
status: Preparing
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-20T00:12:00Z'
updated_date: '2026-11-20T00:58:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 140000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Shutdown is amending the topology plan after rereview on 0b161d3. The public startup guard, current-head validation and closed descriptor design passed review. At the combined configuration-text checker, vary only the candidate configuration identity in one case and only its backend route in another. Keep trusted evidence and every other input fixed. Each mismatch must refuse before projection can use trusted values and conceal the difference. Delivery will submit this delta for review.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The selected two-engine configuration can pass topology validation with exact verified enrollment covering both engines and the shared host partition.
- [ ] #2 The existing dedicated-engine guard still refuses an unverified, partial, mismatched or stale enrollment before lifecycle mutation.
- [ ] #3 Focused offline controls distinguish topology validation from launch authority, readiness and permission to forward.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review of the plan on 0b161d3 requires coverage of the public DTO conversion, router construction and engine startup guards. Amend the internal verifier to compare frozen configuration and target inputs with an independently current revision and digest. Reject old evidence replayed in the same generation. Require authority-issued membership for both exact engines, devices and routes. Exercise those checks offline; public startup continues to refuse the shared topology until a real enrollment producer supplies the evidence.
<!-- SECTION:PLAN:END -->
