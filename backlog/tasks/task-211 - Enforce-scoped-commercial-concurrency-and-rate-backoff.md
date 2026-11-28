---
id: TASK-211
title: "Enforce scoped commercial concurrency and rate backoff"
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-27'
updated_date: '2026-11-28T14:34:28Z'
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
Extend existing route ranking, predecessor checks and admission. Apply model, fit, trust, spend permission and service constraints first. Among eligible choices use configured preference, permitted expected cost, service, affinity/load and stable route ID. Preserve priority, age and ticket deadline. Unknown cost cannot win as zero. A final refusal tries the remaining eligible routes once.

Before dequeue, reserve every applicable provider, stable account, model and key scope atomically for one attempt. Share scope identities across aliases and credential rotation. Offers reserve nothing. Use ordered gates and perform durable I/O outside the queue lock. Bind the first write to the attempt, exact request fingerprint, policy, credentials, config and reservation. Release on proved non-send or provider retirement. Retain unknown occupancy after cancellation, disconnect or reset.

Apply 429 and Retry-After at header receipt. Preserve longer cooldowns and keep billing, authorization, request and service errors distinct. Pin units, window, phase and coverage before increasing available capacity. Subtract outstanding commitments outside that coverage. Use the existing dispatcher wake mechanism and a monotonic clock for the earliest due scope. A reset schedules reevaluation.

TASK-212 owns the shared durable attempt journal. Exercise closed startup, recovered dispositions, blocked and unrelated accounts, and private progress with controlled authority through ordinary routing. Keep production metered and mixed routing disabled. Publish operator aggregates and group-scoped blockers without identifying another group.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review of the dispatcher and direct transport at d045d69 separated socket cleanup from upstream retirement and identified header-time backoff, exact shared-scope admission and recovery as required checks.
<!-- SECTION:NOTES:END -->
