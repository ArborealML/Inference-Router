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
  - TASK-51
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
- [ ] #6 Keep primary DeepSeek resident and streaming while the peer serves both qualified Qwen models in turn through either ingress; verify simultaneous progress, configured fallback and no silent primary eviction.
- [ ] #7 Record actual host/model fit, manager access boundary, protected ingress, external singleton writer and authorized cutover/rollback; failed prerequisites leave deployment unqualified.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
TASK-219 incident diagnosis is independent; verify actual release pins here without requiring reproduction of an unrelated old deployment. Live commissioning is an operator prerequisite, not provisioning implementation. Private-only cluster release does not require full agent policy or commercial integration; positively trusted egress and external protected ingress are required.
<!-- SECTION:NOTES:END -->
