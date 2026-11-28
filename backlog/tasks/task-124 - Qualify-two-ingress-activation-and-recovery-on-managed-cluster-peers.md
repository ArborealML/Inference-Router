---
id: TASK-124
title: Qualify two-ingress activation and recovery on managed cluster peers
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-122
  - TASK-114
  - TASK-118
  - TASK-219
ordinal: 122000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qualify the first useful fixed-placement pair of managed hosts, with one authoritative router. Either ingress reaches its queue. Keep primary inference resident and active while a peer activates another model; no prerequisite for measured placement optimization.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Through either ingress, request a model initially absent from the selected peer. Record host identity, exclusive residency change, verified target readiness and completed inference.
- [ ] #2 Fail one target activation while another request waits. Through the other ingress, observe successful alternate inference under a distinct fleet claim. A host reporting unknown occupancy rejects activation and receives zero launches.
- [ ] #3 Hold a primary or peer stream, switch/drain or refusal and prove other eligible private/remote inference reaches its capture endpoint before release; repeat reverse direction.
- [ ] #4 Partition, stale readiness and restart retain unresolved physical claims. No replay, cross-domain stall or unsafe actuation.
- [ ] #5 Pin installed binaries/configuration and actual host evidence; fixtures alone do not qualify a two-host deployment.
<!-- AC:END -->
