---
id: TASK-211
title: "Enforce scoped commercial concurrency and rate backoff"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-203
  - TASK-210
priority: high
ordinal: 211000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Keep provider/account outstanding attempts and scoped rate observations separate from local slots and spend. Every commercial provider has its own cap; shared-account routes must not double count capacity.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Atomic admission enforces configured provider and applicable shared account/model/key limits across simultaneous routes, including uncertain upstream attempts.
- [ ] #2 Rate and reset semantics use configured scope, window and observation phase; malformed/stale snapshots cannot manufacture headroom.
- [ ] #3 429 and Retry-After cool only the affected scope and wake at the due time; 402, auth, model and service errors stay distinct.
- [ ] #4 Cancel/disconnect frees socket resources but not unproved upstream occupancy; timeout, restart and reconciliation are conservative.
- [ ] #5 Hold a rate-blocked account and prove other eligible commercial and private work dispatches. SPEC §§5–7, 10, 14 and race fixtures cover shared caps.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Start with simple configured concurrency and common rate/backoff controls; add documented rate observations only when needed. Distinguish effective limits from remaining values. Use one next-deadline wake, not synchronous polling on the dispatch path.
<!-- SECTION:PLAN:END -->
