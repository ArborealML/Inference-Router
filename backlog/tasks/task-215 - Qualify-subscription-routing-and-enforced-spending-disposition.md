---
id: TASK-215
title: "Qualify subscription routing and enforced spending disposition"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-213
  - TASK-200
  - TASK-217
  - TASK-218
priority: high
ordinal: 215000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qualify DeepRelay weighted allowance, actual billing disposition and fallback for its authorized account after integrated egress/accounting and subscription fixture work. Isoquant live qualification is independent, not a prerequisite. Do not assume subscription-only enforcement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record actual account terms, shared allowance and reset/weight semantics; controlled authorized results distinguish predicted fit and actual charges.
- [ ] #2 Verify exhaustion/falloff, alternate eligible routing, account-scoped backoff, unknown settlement and paid-overflow permission.
- [ ] #3 Strict subscription-only is enabled only with verified upstream no-overage enforcement; otherwise remain unavailable for that policy.
- [ ] #4 Document actual evidence, fixture-only branches, scope and rollback; no account setting changes or paid tests without approval.
- [ ] #5 Actual included preference and alternative permitted cost ranking match configured policy; no-overage uncertainty requires explicit bounded paid authority or refusal.
- [ ] #6 Record verified protected ingress, singleton writer and authorized root scope for the enabled policy mode; no live call with denied root or spend authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use read-only account evidence where separately authorized, then controlled calls only within verified applicable principal/root scope and affirmative account permission (TASK-217/218). Lowering may remain disabled. Fixture-only or declined approval leaves live release parked. No automatic top-up or account changes. Defer unsupported enforcement instead of fabricating a guarantee.
<!-- SECTION:PLAN:END -->
