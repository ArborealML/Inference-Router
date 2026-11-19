---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T03:42:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-125
  - TASK-110
  - TASK-108
  - TASK-114
  - TASK-25
  - TASK-64
  - TASK-118
ordinal: 110000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake: TASK-125 qualifies the native pair and the listed host and delivery prerequisites land. Prove the first end-to-end native model swap on the primary local host through a real pi-clm client. Both turns and observed model residencies must be on that same host. TASK-85 and TASK-89 established forwarding and client selection. Decision-68 authorizes running local selector engines. Demonstrate the primary-host swap before extending to the peer inventory.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Two same-session Pi/CLM runs passed on the primary host. The first used a temporary AIR listener. The repeat used the existing listener and preserved its process. Each run streamed five text deltas from Qwen and four from Swift, with completed tickets for both models. Before the repeat switch, the queue and all four Qwen slots were idle. The installed selector changed residency. Independent inference confirmed Qwen at 256000 context tokens and four slots, then Swift at 262144 and one slot. The listener finished healthy and drained. A private adapter routed the installed client runtime through AIR. The remaining proof requires router-triggered activation and an AIR mode in the installed launcher.
<!-- SECTION:NOTES:END -->
