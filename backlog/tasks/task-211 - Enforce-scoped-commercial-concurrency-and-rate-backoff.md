---
id: TASK-211
title: "Enforce scoped commercial concurrency and rate backoff"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-20
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
- [ ] #6 Among simultaneously ready equivalent routes, rank allowed private/metered choices by configured preference, expected permitted cost and service constraints; busy preferred routes never hide another admissible choice.
- [ ] #7 Changing only priority changes competing pending order, not route eligibility, cost ranking, spend permission or provider capacity. Test deterministic ties and wait-versus-spend policy.
- [ ] #8 Existing status/events and CLI/monitor read models report scoped caps, blockers, reset/freshness and backoff without leaking other principals' metadata.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend existing route comparison: hard model/fit/trust/filter and scoped capacity, then explicit standing spend permission, wait/service constraints and deterministic expected marginal cost/private preference. No global lock over telemetry or preparation; only deliberately shared caps serialize. Use common backoff/deadline wake and synthetic policy/cost/admission snapshots; definitive reservation refusal refreshes/excludes that attempt and resumes the same finite selection round.

TASK-213 adds included preference to this same evaluator; no generic optimizer. TASK-212 integrates real durable reservations and authorization; no live enablement from this task.
<!-- SECTION:PLAN:END -->
