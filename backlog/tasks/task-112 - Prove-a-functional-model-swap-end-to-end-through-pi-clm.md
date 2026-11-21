---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: In Progress
assignee:
  - '@gpt-6.2-sol-frames'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-21T05:16:00Z'
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
Two staged Pi sessions completed through one ordinary AIR listener while a synthetic host switched models. Qwen streamed before the DeepSeek request queued. Qwen finished and its physical use retired before the simulated stop and start. AIR served the original DeepSeek ticket after readiness. Stock Pi can change the selected model during an active turn. Source shows it buffers a steered follow-up locally. The next scoped case observes when that same session sends its second HTTP request to AIR. The live physical switch remains open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [ ] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse the staged paired Pi driver with the verified one-slot successor in one ordinary HTTP listener. Hold the incumbent stream until the cold request queues. Join each actual request and session to its own ticket. Observe the incumbent physical use retire before the switch, then verify readiness, first write and streamed completion for the original queued ticket. Record observed context headers and estimated admission where omitted. The offline integration plan passed review and the isolated implementation slot is released.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The joined run used natural context estimates and two completion requests. Each session retained its own ticket through completion. Earlier paired sessions covered overlapping same-model, follow-up and mixed-model requests. Current launcher work reconciles the staged model list with the operator configuration. Live acceptance requires observed backend residency on the deployed host.
<!-- SECTION:NOTES:END -->
