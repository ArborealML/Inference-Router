---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: Waiting
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-20T00:52:00Z'
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
  - TASK-143
ordinal: 110000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Waiting for independent review of the amended offline Pi client plan on 0b161d3. The original review returned no verdict and its continuation was unavailable. Delivery submitted the same amendment once more; the verdict wakes Scheduling. Local Git is the source of record for this temporary launcher. Implementation and deployment have separate gates; the live Pi swap criteria remain open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Prove the full eligible provider/model set in one isolated Pi RPC process. Check launcher arguments, credentials and resource isolation. Bound loopback redirection and observe outbound requests. Select both canonical model IDs in the same process and session; verify that the second request carries the previous user and assistant messages. Preserve ordinary launcher behavior.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Two same-session Pi/CLM runs passed on the primary host. The first used a temporary AIR listener. The repeat used the existing listener and preserved its process. Each run streamed five text deltas from Qwen and four from Swift, with completed tickets for both models. Before the repeat switch, the queue and all four Qwen slots were idle. The installed selector changed residency. Independent inference confirmed Qwen at 256000 context tokens and four slots, then Swift at 262144 and one slot. The listener finished healthy and drained. A private adapter routed the installed client runtime through AIR. The remaining proof requires router-triggered activation and an AIR mode in the installed launcher.
<!-- SECTION:NOTES:END -->
