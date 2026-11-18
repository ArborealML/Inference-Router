---
id: TASK-69
title: >-
  Design the switching router: never stuck, sequenced capacity, measured switch
  cost
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T18:47:00Z'
labels: []
dependencies:
  - TASK-117
priority: high
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner must choose the same-target retry policy in TASK-117. The design review has returned. After the ruling, the coordinator records the selected policy and reconciles it with the existing no-retry contract.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the plan sits in this row before any code, and the design review at the required tier has returned and been dispositioned
- [ ] #2 the plan names what a failed swap does to in-flight and to waiting requests, in wire and feed terms
- [ ] #3 the plan honours four lanes for the whole box and one loaded model at a time, and says how capacity is sequenced
- [ ] #4 the posture rows this goal un-parks are answered or explicitly kept open by the plan, not worked around
- [ ] #5 Automatic switching is a core routing capability. After a failed swap, pending work for other demonstrably available models is served, with an observable outcome for the failed target.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Define failure outcomes for admitted and waiting requests, wire errors and feed events. A failed swap clears its claim, settles waiters and serves only proven capacity. Apply the owner’s retry ruling from TASK-117. Design for four lanes on one host with one resident model. Reserve lifecycle work separately and fence admission before draining (25/64). Measure per-model activation and shutdown cost, including cold or sparse observations (71). Schedule age-weighted batches by work unlocked per second of switching, with a bounded wait, observable refusal and switch-rate bound even for streaming workloads (51/72). Read one capability snapshot (68). Distinguish reach from load authority (77), fence host ownership, and defer fleet placement to 78.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
An external evaluation of an open-source AI gateway (2026-11-18) found nothing that makes this design redundant: no durable request claims, no residence placement or fencing across hosts, a switch-cost gate priced in prompt-cache dollars rather than stop and load time, and nothing that waits for confirmed serving. Its challenge — measurement needs no database and no wire change, so it should not wait behind this row — is recorded on TASK-71.

Drift review checked at 2d50430. Settle failure policy first, then coordinate exclusive residency across engines on one host (TASK-110), close native-provider gaps (26), fence drain and admission (25/64), and bound waits and schedule by measured cost (51/71/72). Review reload (107) before the ownership split and fleet placement (77/78). Front-only proofs (85/89) can proceed after the strict-client keepalive fix (108). The local configuration (111) and an authorized pi-clm swap (112) gate lock cleanup (113).

A bounded review of the three board commits ending at 60849d9 was submitted on 2026-11-18 for lifecycle permission, task dependencies and evidence retention.

Failure-policy review at e0d3f02: fail A after the incumbent stops with B waiting. Verify B inference under a fresh ownership-checked transaction, one terminal per affected A ticket, and no unproven launch. Test wakeups and explicit reset against the selected retry limit. TASK-51 covers bounded waits; TASK-25/64 protect four lanes and drain; TASK-71/72 cover cost and age. Suggested timeouts await measurement. Ending a committed stream to enforce finite drain requires a separate wire decision.

With eight B waiters, one A waiter and four lanes, test two B waves followed by the aged A turn (TASK-72). An unbounded stream invalidates a universal service-time guarantee; distinguish deadline/refusal from guaranteed service.
<!-- SECTION:NOTES:END -->
