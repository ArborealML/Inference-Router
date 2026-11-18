---
id: TASK-69
title: >-
  Design the switching router: never stuck, sequenced capacity, measured switch
  cost
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T18:21:00Z'
labels: []
dependencies: []
priority: high
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner's operating goal is recorded in decision-25: switching across the board, a router that is never stuck after a failed swap, capacity from sequencing models rather than running them together, and switch cost measured rather than assumed. This row is the design, and it comes before code: no scheduling, lifecycle or queue change lands from this goal until the plan is here and a design review at the tier this contract requires has returned. The design has to take a hard constraint seriously — four concurrent request lanes for the whole box, one engine loaded at a time. Wake condition: the top-tier design pass's verdict, recorded in this row; the plan is drafted before the verdict and revised after it.
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
Define failure outcomes for admitted and waiting requests, wire errors and feed events. A failed swap clears its claim, settles waiters and serves only proven capacity. Specify when a later request may retry (TASK-67). Design for four lanes on one host with one resident model. Reserve lifecycle work separately and fence admission before draining (25/64). Measure per-model activation and shutdown cost, including cold or sparse observations (71). Schedule age-weighted batches by work unlocked per second of switching, with a bounded wait, observable refusal and switch-rate bound even for streaming workloads (51/72). Read one capability snapshot (68). Distinguish reach from load authority (77), fence host ownership, and defer fleet placement to 78.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
An external evaluation of an open-source AI gateway (2026-11-18) found nothing that makes this design redundant: no durable request claims, no residence placement or fencing across hosts, a switch-cost gate priced in prompt-cache dollars rather than stop and load time, and nothing that waits for confirmed serving. Its challenge — measurement needs no database and no wire change, so it should not wait behind this row — is recorded on TASK-71.

Drift review checked at 2d50430. Settle failure policy first, then coordinate exclusive residency across engines on one host (TASK-110), close native-provider gaps (26), fence drain and admission (25/64), and bound waits and schedule by measured cost (51/71/72). Review reload (107) before the ownership split and fleet placement (77/78). Front-only proofs (85/89) can proceed after the strict-client keepalive fix (108). The local configuration (111) and an authorized pi-clm swap (112) gate lock cleanup (113).

A bounded review of the three board commits ending at 60849d9 was submitted on 2026-11-18 for lifecycle permission, task dependencies and evidence retention.
<!-- SECTION:NOTES:END -->
