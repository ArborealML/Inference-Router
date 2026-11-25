---
id: TASK-124
title: Qualify two-ingress activation and recovery on managed cluster peers
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:32:00Z'
updated_date: '2026-11-25T02:28:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-123
  - TASK-114
  - TASK-118
ordinal: 122000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Exercise the assembled TASK-78 cluster path on a qualified pair of hosts. Route through both ingress endpoints and observe a managed peer change residence under its own authority.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Through either ingress, request a model initially absent from the selected peer. Record host identity, exclusive residency change, verified target readiness and completed inference.
- [ ] #2 Fail one target activation while another request waits. Through the other ingress, observe successful alternate inference under a distinct fleet claim. A host reporting unknown occupancy rejects activation and receives zero launches.
- [ ] #3 On one GPU shared by native Windows Strata and WSL2 HyperQwen, ordinary queued requests drive switches in both directions. In each direction the incumbent finishes first, releases the GPU, and the original waiting request streams from the verified target.
- [ ] #4 Record the effective HyperQwen context window on the WSL2 host and verify readiness against the advertised profile before forwarding. A smaller observed window withdraws that profile from placement.
<!-- AC:END -->
