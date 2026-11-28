---
id: TASK-212
title: "Reserve and settle bounded commercial spend durably"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-20
dependencies:
  - TASK-208
  - TASK-210
  - TASK-211
priority: high
ordinal: 212000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Own integrated commercial send safety: one attempt and durable maximum exposure before any possible upstream send; then consume TASK-208's final authorization. Reuse existing transport and scoped admission. No general financial service; existing legacy commercial routes must migrate to this path before hybrid enablement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Freeze exact projected body, destination/account/config, scope reservations, price bound and policy revision; atomic concurrent admission cannot double spend.
- [ ] #2 Persist maximum possible charge before submission; recheck current policy/revocations and consume one authorization after persistence, never reuse it.
- [ ] #3 Hard budget needs conservative billable input, enforceable aggregate output/reasoning/choices and maximum cache/price/fee bounds; unknown bounds refuse.
- [ ] #4 Crash, cancel, reset, removal or unverified restore retain unresolved occupancy/liability until proven settlement or audited correction; never expire unknown exposure.
- [ ] #5 Ambiguous submission never automatically replays; separately authorized replacement reserves independently and never splices output.
- [ ] #6 Every owned-policy/hybrid commercial path, including legacy and count/tokenize, is fail-closed until the integrated guard is qualified; unclassified destinations always refuse and rollback cannot bypass classification or guard.
- [ ] #7 Barrier tests cover persistence/escalation/send/reload order, shared-account races, definite non-send and unknown outcomes; held accounting cannot stall unrelated domains.
- [ ] #8 SPEC §§5–7, 10, 14 pin guarantees and migration; no live provider call required.
- [ ] #9 Race preferred-route liability/cap refusal after ranking; another permitted ready route progresses within the round, or scoped denial results; no duplicate reservation, spin or unauthorized spend.
- [ ] #10 Require externally established singleton writer and exclusive journal opening where shared; duplicate start refuses and unresolved old-writer authority prevents automatic takeover.
- [ ] #11 Expose scoped predicted/settled/unresolved liability and attempt disposition through existing status/events and CLI/monitor, without prompts or secret metadata.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Compute an integer maximum charge from conservative input, aggregate output including reasoning and choices, cache and tariff bounds. Refuse missing bounds or overflow. Before dequeue, atomically reserve across all canonical budget scopes. Settled charges, unresolved maxima, provisional reservations and the new maximum must fit every limit. Persist the entire reservation vector as one attempt. Confirm its durable publication and writer generation before issuing a one-use capability. Other accounts and private routes can progress during persistence.

At the first application write, verify the exact attempt, request, destination, policy, account reservations, credentials, writer and cancellation together. A crash after durable authorization leaves possible exposure even if no later send record exists. Recovery restores commitments only. Require proven non-send or verified billing settlement before reducing liability. Persist releases before returning headroom. Provider retirement may independently release occupancy. Ambiguity forbids replay. Keep the singleton writer through late I/O and refuse unverified restores.

Exercise the existing dispatcher, protected HTTP writer and scoped read models with controlled accounts. Disable or migrate unclassified and legacy egress before hybrid enablement. Rollback requires external credential or egress fencing. TASK-214/215 own authorized deployment checks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review at d045d69 confirmed the transport’s first-write boundary and identified the separate durable exposure receipt, tariff proof and recovery requirements. Reuse the same attempt identifier as scoped concurrency. Manual reconciliation is sufficient initially.
<!-- SECTION:NOTES:END -->
