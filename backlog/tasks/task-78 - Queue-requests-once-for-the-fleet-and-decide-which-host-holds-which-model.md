---
id: TASK-78
title: Queue requests once for the fleet and decide which host holds which model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T19:29:00Z'
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
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Deliberately coarse until TASK-69's design lands and TASK-77 has split reach from ownership: residence is meaningless while one flag decides whether a host can be switched at all. Expected to split into implementation rows once reviewed — a fleet queue that admits against fleet capacity, a residence policy that consumes switch cost, and the ceiling rule for a request that declared no max_queue_time (TASK-51 decides that number). Milestone-level means nobody should start coding it from this text.

Cross-host forwarding verified on 2026-11-18 with an isolated router and an existing remote Ternary Bonsai server over an SSH tunnel. Ordinary discovery returned the exact model ID and engine, and streamed Chat returned HTTP 200, answer 5, finish reason stop and [DONE] in 1.61 seconds. A 24-token attempt completed without visible text. This exercised one ingress and an already-resident model. Shared admission, either-ingress routing, peer activation and recovery remain unproved.
<!-- SECTION:NOTES:END -->
