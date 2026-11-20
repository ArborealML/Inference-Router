---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: Blocked
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-20T00:26:00Z'
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
Blocked on Claire choosing the launcher source of record in TASK-143 before implementation. The independent client plan review on 0b161d3 can complete meanwhile. Exercise opt-in AIR selection with one Pi RPC process, a fake roster initially containing only the incumbent, exact model requests and retained conversation history. Keep live residency criteria open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Identify ownership of the launcher and its generated model catalog. Propose the minimum integration and evidence of two requested model IDs in successive turns through one AIR endpoint. Preserve direct-backend launch behavior and record actual host residency only during the separately gated live run.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Two same-session Pi/CLM runs passed on the primary host. The first used a temporary AIR listener. The repeat used the existing listener and preserved its process. Each run streamed five text deltas from Qwen and four from Swift, with completed tickets for both models. Before the repeat switch, the queue and all four Qwen slots were idle. The installed selector changed residency. Independent inference confirmed Qwen at 256000 context tokens and four slots, then Swift at 262144 and one slot. The listener finished healthy and drained. A private adapter routed the installed client runtime through AIR. The remaining proof requires router-triggered activation and an AIR mode in the installed launcher.
<!-- SECTION:NOTES:END -->
