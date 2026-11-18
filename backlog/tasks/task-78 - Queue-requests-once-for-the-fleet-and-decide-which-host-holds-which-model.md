---
id: TASK-78
title: Queue requests once for the fleet and decide which host holds which model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T20:55:00Z'
labels:
  - scheduling
milestone: m-6
dependencies:
  - TASK-77
  - TASK-69
priority: high
ordinal: 78000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
decision-25 parked a fleet-wide queue and cross-host model residence as later work; the owner asked for both as a milestone row now, to start after the safety-gate work. This is the umbrella the parts hang from: measured switch cost (TASK-71), age-weighted draining (TASK-72) and the waiting local proxy (TASK-73) are each one behaviour, and none of them is a scheduler. The act to reach is: a request waits for the fleet's best answer rather than one engine's, and the router decides which host holds which model.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A waiting request is admitted against fleet capacity rather than one engine's queue
- [ ] #2 Residence names which host holds which model and when it moves, with measured switch cost as the input
- [ ] #3 The age-weighted drain applies fleet-wide, and a stated ceiling covers a request that declared none
- [ ] #4 SPEC carries acceptance criteria a fleet scheduler has to meet before any implementation row starts
- [ ] #5 A request arriving at any cluster ingress can reach an eligible host selected from the global request queue. A managed peer can automatically switch models under its proven lifecycle authority.
- [ ] #6 Fleet placement selects host, model and batch. The host authority controls stop, ownership and admission. A missed heartbeat cannot prove vacancy or override a local refusal.
- [ ] #7 When an eligible host loses its warm copy, recompute placement using measured reload time or an explicit unknown. A lower predicted cost elsewhere cannot override that host’s admission refusal.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
TASK-122 owns shared admission and ticket claims. TASK-123 owns measured placement and service for aged requests. TASK-124 qualifies managed activation and recovery through both ingress endpoints. SPEC sections 12 and 14 must identify this milestone and its implementation rows. TASK-77 controls lifecycle authority and TASK-110 controls physical host occupancy.

At 31179e3 one live ingress served turns on resident Qwen and a remote resident Bonsai model in the same pi session. The tests for shared admission, either-ingress placement, peer activation and recovery remain open.

Before implementation, SPEC must define shared ticket identity and claim epochs, global capacity, restart and partition handling, cancellation, terminal-result ownership and the host veto. Closure requires TASK-122 through TASK-124, including real two-ingress qualification.
<!-- SECTION:NOTES:END -->
