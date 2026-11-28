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
  - TASK-214
priority: high
ordinal: 215000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After bounded metered operation, qualify DeepRelay weighted allowance, actual billing disposition and fallback for the authorized account. Do not assume plan entitlement or enforceable subscription-only mode.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record actual account terms, shared allowance and reset/weight semantics; controlled authorized results distinguish predicted fit and actual charges.
- [ ] #2 Verify exhaustion/falloff, alternate eligible routing, account-scoped backoff, unknown settlement and paid-overflow permission.
- [ ] #3 Strict subscription-only is enabled only with verified upstream no-overage enforcement; otherwise remain unavailable for that policy.
- [ ] #4 Document actual evidence, fixture-only branches, scope and rollback; no account setting changes or paid tests without approval.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use read-only account evidence first where authorized, then only controlled requests within TASK-218. No automatic top-up changes. Defer unsupported enforcement instead of fabricating a guarantee.
<!-- SECTION:PLAN:END -->
