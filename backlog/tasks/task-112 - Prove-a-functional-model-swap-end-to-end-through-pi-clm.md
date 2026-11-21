---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: In Progress
assignee:
  - '@gpt-6.2-sol-frames'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-21T04:15:00Z'
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
Seven staged completion requests carried their expected session IDs across same-model, follow-up, mixed-model and new-session turns. Discovery omitted the header. All five refusal cases stopped before fetch and returned a generic client error. One staged Pi request passed through the ordinary AIR listener with a synthetic upstream. Its exact session identity matched the retained ticket through completion. The paired plan joins two sessions on this listener and waits for idle capacity before each new pair. The primary-host run still requires commissioned host enrollment, cold queue activation and verified readiness.
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
Earlier primary-host Pi runs streamed Qwen and Swift through AIR using an installed selector. The staged launcher subsequently supplied stable router session IDs. Two staged Pi sessions completed six requests through ordinary AIR with a synthetic upstream: overlapping same-model, follow-up and mixed-model pairs. The router retained both identities, issued six upstream attempts and replayed each ticket through completion. No physical model was swapped in this run. The next integration joins this client path to queue-triggered cold activation.
<!-- SECTION:NOTES:END -->
